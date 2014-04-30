//-------------------------------------------------------------------------------------------------
// <copyright file="retina.cs" company="Outercurve Foundation">
//   Copyright (c) 2004, Outercurve Foundation.
//   This software is released under Microsoft Reciprocal License (MS-RL).
//   The license and further copyright text can be found in the file
//   LICENSE.TXT at the root directory of the distribution.
// </copyright>
// 
// <summary>
// Tool to extract files from binary Wixlibs and rebuild those Wixlibs with updated files
// </summary>
//-------------------------------------------------------------------------------------------------

namespace WixToolset.Tools
{
    using System;
    using System.Collections.Specialized;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Globalization;
    using System.Runtime.InteropServices;
    using System.Text.RegularExpressions;

    using WixToolset.Cab;
    using WixToolset.Data;
    using WixToolset.Extensibility;

    /// <summary>
    /// Entry point for the library rebuilder
    /// </summary>
    public sealed class Retina
    {
        private StringCollection invalidArgs;
        private string inputFile;
        private string outputFile;
        private bool showHelp;
        private bool showLogo;

        /// <summary>
        /// Instantiate a new Retina class.
        /// </summary>
        private Retina()
        {
            this.invalidArgs = new StringCollection();
            this.showLogo = true;
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        /// <param name="args">Arguments to decompiler.</param>
        /// <returns>0 if sucessful, otherwise 1.</returns>
        public static int Main(string[] args)
        {
            AppCommon.PrepareConsoleForLocalization();
            Messaging.Instance.InitializeAppName("RETI", "retina.exe").Display += Retina.DisplayMessage;

            Retina retina = new Retina();
            return retina.Run(args);
        }

        /// <summary>
        /// Handler for display message events.
        /// </summary>
        /// <param name="sender">Sender of message.</param>
        /// <param name="e">Event arguments containing message to display.</param>
        private static void DisplayMessage(object sender, DisplayEventArgs e)
        {
            Console.WriteLine(e.Message);
        }

        /// <summary>
        /// Main running method for the application.
        /// </summary>
        /// <param name="args">Commandline arguments to the application.</param>
        /// <returns>Returns the application error code.</returns>
        private int Run(string[] args)
        {
            try
            {
                // parse the command line
                this.ParseCommandLine(args);

                // exit if there was an error parsing the command line (otherwise the logo appears after error messages)
                if (Messaging.Instance.EncounteredError)
                {
                    return Messaging.Instance.LastErrorNumber;
                }

                if (!(String.IsNullOrEmpty(this.inputFile) ^ String.IsNullOrEmpty(this.outputFile)))
                {
                    this.showHelp = true;
                }

                if (this.showLogo)
                {
                    AppCommon.DisplayToolHeader();
                }

                if (this.showHelp)
                {
                    Console.WriteLine(RetinaStrings.HelpMessage);
                    AppCommon.DisplayToolFooter();
                    return Messaging.Instance.LastErrorNumber;
                }

                foreach (string parameter in this.invalidArgs)
                {
                    Messaging.Instance.OnMessage(WixWarnings.UnsupportedCommandLineArgument(parameter));
                }
                this.invalidArgs = null;

                if (!String.IsNullOrEmpty(this.inputFile))
                {
                    this.ExtractBinaryWixlibFiles();
                }
                else
                {
                    this.RebuildWixlib();
                }
            }
            catch (WixException we)
            {
                Messaging.Instance.OnMessage(we.Error);
            }
            catch (Exception e)
            {
                Messaging.Instance.OnMessage(WixErrors.UnexpectedException(e.Message, e.GetType().ToString(), e.StackTrace));
                if (e is NullReferenceException || e is SEHException)
                {
                    throw;
                }
            }

            return Messaging.Instance.LastErrorNumber;
        }

        /// <summary>
        /// Extracts files from a binary Wixlib.
        /// </summary>
        public void ExtractBinaryWixlibFiles()
        {
            Dictionary<int, string> mapCabinetFileIdToFileName = Retina.GetCabinetFileIdToFileNameMap(this.inputFile);
            if (0 == mapCabinetFileIdToFileName.Count)
            {
                Messaging.Instance.OnMessage(WixWarnings.NotABinaryWixlib(this.inputFile));
                return;
            }

            // extract the files using their cabinet names ("0", "1", etc.)
            using (WixExtractCab extractor = new WixExtractCab())
            {
                extractor.Extract(this.inputFile, Path.GetDirectoryName(this.inputFile));
            }

            // the same file can be authored multiple times in the same Wixlib
            Dictionary<string, bool> uniqueFiles = new Dictionary<string, bool>();

            // rename those files to what was authored
            foreach (KeyValuePair<int, string> kvp in mapCabinetFileIdToFileName)
            {
                string cabinetFileId = Path.Combine(Path.GetDirectoryName(this.inputFile), kvp.Key.ToString());
                string fileName = Path.Combine(Path.GetDirectoryName(this.inputFile), kvp.Value);

                uniqueFiles[fileName] = true;

                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(this.inputFile), Path.GetDirectoryName(fileName)));
                if (File.Exists(fileName))
                {
                    File.Delete(fileName);
                }

                File.Move(cabinetFileId, fileName);
            }

            foreach (string fileName in uniqueFiles.Keys)
            {
                Console.WriteLine(fileName);
            }
        }

        /// <summary>
        /// Rebuild the Wixlib using the original Wixlib and updated files.
        /// </summary>
        private void RebuildWixlib()
        {
            if (0 == Retina.GetCabinetFileIdToFileNameMap(this.outputFile).Count)
            {
                Messaging.Instance.OnMessage(WixWarnings.NotABinaryWixlib(this.outputFile));
                return;
            }

            Librarian librarian = new Librarian();
            Library library = Library.Load(this.outputFile, librarian.TableDefinitions, false);
            LibraryBinaryFileResolver resolver = new LibraryBinaryFileResolver() { FileManager = new BlastBinderFileManager(this.outputFile) };
            library.Save(this.outputFile, resolver);
        }

        /// <summary>
        /// Map from cabinet file ids to a normalized relative path.
        /// </summary>
        /// <param name="path">Path to Wixlib.</param>
        /// <returns>Returns the map.</returns>
        private static Dictionary<int, string> GetCabinetFileIdToFileNameMap(string path)
        {
            Dictionary<int, string> mapCabinetFileIdToFileName = new Dictionary<int, string>();
            BlastBinderFileManager binderFileManager = new BlastBinderFileManager(path);
            Librarian librarian = new Librarian();
            Library library = Library.Load(path, librarian.TableDefinitions, false);

            foreach (Section section in library.Sections)
            {
                foreach (Table table in section.Tables)
                {
                    foreach (Row row in table.Rows)
                    {
                        foreach (Field field in row.Fields)
                        {
                            ObjectField objectField = field as ObjectField;

                            if (null != objectField && null != objectField.Data)
                            {
                                string filePath = binderFileManager.ResolveFile(objectField.Data as string, "source", row.SourceLineNumbers, BindStage.Normal);
                                mapCabinetFileIdToFileName[objectField.EmbeddedFileIndex.Value] = filePath;
                            }
                        }
                    }
                }
            }

            return mapCabinetFileIdToFileName;
        }

        /// <summary>
        /// Parse the commandline arguments.
        /// </summary>
        /// <param name="args">Commandline arguments.</param>
        private void ParseCommandLine(string[] args)
        {
            for (int i = 0; i < args.Length; ++i)
            {
                string arg = args[i];
                if (null == arg || 0 == arg.Length) // skip blank arguments
                {
                    continue;
                }

                if ('-' == arg[0] || '/' == arg[0])
                {
                    string parameter = arg.Substring(1);

                    if ("nologo" == parameter)
                    {
                        this.showLogo = false;
                    }
                    else if ("i" == parameter || "in" == parameter)
                    {
                        this.inputFile = CommandLine.GetFile(parameter, args, ++i);

                        if (String.IsNullOrEmpty(this.inputFile))
                        {
                            return;
                        }
                    }
                    else if ("o" == parameter || "out" == parameter)
                    {
                        this.outputFile = CommandLine.GetFile(parameter, args, ++i);

                        if (String.IsNullOrEmpty(this.outputFile))
                        {
                            return;
                        }
                    }
                    else if ("v" == parameter)
                    {
                        Messaging.Instance.ShowVerboseMessages = true;
                    }
                    else if ("?" == parameter || "help" == parameter)
                    {
                        this.showHelp = true;
                        return;
                    }
                    else
                    {
                        this.invalidArgs.Add(parameter);
                    }
                }
            }
        }

        /// <summary>
        /// A custom binder file manager that returns a normalized path for both extracting files
        /// from a Wixlib and for finding them when rebuilding the same Wixlib.
        /// </summary>
        class BlastBinderFileManager : BinderFileManager
        {
            private string basePath = null;

            // shamelessly stolen from wix\Common.cs
            private static readonly Regex WixVariableRegex = new Regex(@"(\!|\$)\((?<namespace>loc|wix|bind|bindpath)\.(?<fullname>(?<name>[_A-Za-z][0-9A-Za-z_]+)(\.(?<scope>[_A-Za-z][0-9A-Za-z_\.]*))?)(\=(?<value>.+?))?\)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture);

            public BlastBinderFileManager(string path)
            {
                this.basePath = Path.GetDirectoryName(path);
            }

            /// <summary>
            /// Resolves the source path of a file to a normalized path relative to the Wixlib.
            /// </summary>
            /// <param name="source">Original source value.</param>
            /// <param name="type">Optional type of source file being resolved.</param>
            /// <param name="sourceLineNumbers">Optional source line of source file being resolved.</param>
            /// <param name="bindStage">The binding stage used to determine what collection of bind paths will be used</param>
            /// <returns>Should return a valid path for the stream to be imported.</returns>
            public override string ResolveFile(string source, string type, SourceLineNumber sourceLineNumbers, BindStage bindStage)
            {
                Match match = BlastBinderFileManager.WixVariableRegex.Match(source);
                if (match.Success)
                {
                    string variableNamespace = match.Groups["namespace"].Value;
                    if ("wix" == variableNamespace && match.Groups["value"].Success)
                    {
                        source = match.Groups["value"].Value;
                    }
                    else if ("bindpath" == variableNamespace)
                    {
                        string dir = String.Concat("bindpath_", match.Groups["fullname"].Value);
                        // bindpaths might or might not be followed by a backslash, depending on the pedantic nature of the author
                        string file = source.Substring(match.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        source = Path.Combine(dir, file);
                    }
                }

                if (Path.IsPathRooted(source))
                {
                    source = Path.GetFileName(source);
                }

                if (source.StartsWith("SourceDir\\", StringComparison.Ordinal) || source.StartsWith("SourceDir/", StringComparison.Ordinal))
                {
                    source = source.Substring(10);
                }

                return Path.Combine(this.basePath, source);
            }
        }

        private class LibraryBinaryFileResolver : ILibraryBinaryFileResolver
        {
            public BinderFileManager FileManager { get; set; }

            public string Resolve(SourceLineNumber sourceLineNumber, string table, string path)
            {
                return this.FileManager.ResolveFile(path, table, sourceLineNumber, BindStage.Normal);
            }
        }
    }
}