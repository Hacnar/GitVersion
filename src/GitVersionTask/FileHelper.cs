using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;

namespace GitVersion.MSBuildTask
{
    public static class FileHelper
    {
        private static readonly Dictionary<string, Func<string, string, bool>> VersionAttributeFinders = new Dictionary<string, Func<string, string, bool>>
        {
            { ".cs", CSharpFileContainsVersionAttribute },
            { ".vb", VisualBasicFileContainsVersionAttribute }
        };

        public static string TempPath;

        static FileHelper()
        {
            TempPath = Path.Combine(Path.GetTempPath(), "GitVersionTask");
            Directory.CreateDirectory(TempPath);
        }

        public static void DeleteTempFiles()
        {
            if (!Directory.Exists(TempPath))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(TempPath))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-1))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        //ignore contention
                    }
                }
            }
        }

        public static string GetFileExtension(string language)
        {
            return language switch
            {
                "C#" => "cs",
                "F#" => "fs",
                "VB" => "vb",
                _ => throw new ArgumentException($"Unknown language detected: '{language}'")
            };
        }

        public static void CheckForInvalidFiles(IEnumerable<ITaskItem> compileFiles, string projectFile)
        {
            foreach (var compileFile in GetInvalidFiles(compileFiles, projectFile))
            {
                throw new WarningException("File contains assembly version attributes which conflict with the attributes generated by GitVersion " + compileFile);
            }
        }

        private static bool FileContainsVersionAttribute(string compileFile, string projectFile)
        {
            var compileFileExtension = Path.GetExtension(compileFile);

            if (VersionAttributeFinders.TryGetValue(compileFileExtension, out var languageSpecificFileContainsVersionAttribute))
            {
                return languageSpecificFileContainsVersionAttribute(compileFile, projectFile);
            }

            throw new WarningException("File with name containing AssemblyInfo could not be checked for assembly version attributes which conflict with the attributes generated by GitVersion " + compileFile);
        }

        private static bool CSharpFileContainsVersionAttribute(string compileFile, string projectFile)
        {
            var combine = Path.Combine(Path.GetDirectoryName(projectFile), compileFile);
            var allText = File.ReadAllText(combine);

            allText += System.Environment.NewLine; // Always add a new line, this handles the case for when a file ends with the EOF marker and no new line. If you don't have this newline, the regex will match commented out Assembly*Version tags on the last line.

            var blockComments = @"/\*(.*?)\*/";
            var lineComments = @"//(.*?)\r?\n";
            var strings = @"""((\\[^\n]|[^""\n])*)""";
            var verbatimStrings = @"@(""[^""]*"")+";

            var noCommentsOrStrings = Regex.Replace(allText,
                blockComments + "|" + lineComments + "|" + strings + "|" + verbatimStrings,
                me => me.Value.StartsWith("//") ? System.Environment.NewLine : string.Empty,
                RegexOptions.Singleline);

            return Regex.IsMatch(noCommentsOrStrings, @"(?x) # IgnorePatternWhitespace

\[\s*assembly\s*:\s*                    # The [assembly: part

(System\s*\.\s*Reflection\s*\.\s*)?     # The System.Reflection. part (optional)

Assembly(File|Informational)?Version    # The attribute AssemblyVersion, AssemblyFileVersion, or AssemblyInformationalVersion

\s*\(\s*\)\s*\]                         # End brackets ()]");
        }

        private static bool VisualBasicFileContainsVersionAttribute(string compileFile, string projectFile)
        {
            var combine = Path.Combine(Path.GetDirectoryName(projectFile), compileFile);
            var allText = File.ReadAllText(combine);

            allText += System.Environment.NewLine; // Always add a new line, this handles the case for when a file ends with the EOF marker and no new line. If you don't have this newline, the regex will match commented out Assembly*Version tags on the last line.

            var lineComments = @"'(.*?)\r?\n";
            var strings = @"""((\\[^\n]|[^""\n])*)""";

            var noCommentsOrStrings = Regex.Replace(allText,
                lineComments + "|" + strings,
                me => me.Value.StartsWith("'") ? System.Environment.NewLine : string.Empty,
                RegexOptions.Singleline);

            return Regex.IsMatch(noCommentsOrStrings, @"(?x) # IgnorePatternWhitespace

\<\s*Assembly\s*:\s*                    # The <Assembly: part

(System\s*\.\s*Reflection\s*\.\s*)?     # The System.Reflection. part (optional)

Assembly(File|Informational)?Version    # The attribute AssemblyVersion, AssemblyFileVersion, or AssemblyInformationalVersion

\s*\(\s*\)\s*\>                         # End brackets ()>");
        }

        private static IEnumerable<string> GetInvalidFiles(IEnumerable<ITaskItem> compileFiles, string projectFile)
        {
            return compileFiles.Select(x => x.ItemSpec)
                .Where(compileFile => compileFile.Contains("AssemblyInfo"))
                .Where(s => FileContainsVersionAttribute(s, projectFile));
        }

        public static FileWriteInfo GetFileWriteInfo(this string intermediateOutputPath, string language, string projectFile, string outputFileName)
        {
            var fileExtension = GetFileExtension(language);
            string workingDirectory, fileName;

            if (intermediateOutputPath == null)
            {
                fileName = $"{outputFileName}_{Path.GetFileNameWithoutExtension(projectFile)}_{Path.GetRandomFileName()}.g.{fileExtension}";
                workingDirectory = TempPath;
            }
            else
            {
                fileName = $"{outputFileName}.g.{fileExtension}";
                workingDirectory = intermediateOutputPath;
            }
            return new FileWriteInfo(workingDirectory, fileName, fileExtension);
        }
    }
}
