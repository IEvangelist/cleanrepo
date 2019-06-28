using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CleanRepo
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1304:Specify CultureInfo", Justification = "Annoying")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1307:Specify StringComparison", Justification = "Annoying")]
    class Program
    {
        static void Main(string[] args)
        {
            // Command line options
            var options = new Options();
            var parsedArgs = CommandLine.Parser.Default.ParseArguments(args, options);

            if (parsedArgs)
            {
                // Verify that the input directory exists.
                if (!Directory.Exists(options.InputDirectory))
                {
                    Console.WriteLine($"\nDirectory '{options.InputDirectory}' does not exist.");
                    return;
                }

                var stopwatch = new Stopwatch();
                stopwatch.Start();

                // Find orphaned topics
                if (options.FindOrphanedTopics)
                {
                    Console.WriteLine($"\nSearching the '{options.InputDirectory}' directory and its subdirectories for orphaned topics...");

                    var tocFiles = GetTocFiles(options.InputDirectory);
                    var markdownFiles = GetMarkdownFiles(options.InputDirectory, options.SearchRecursively);

                    ListOrphanedTopics(tocFiles, markdownFiles, options.Delete);
                }
                // Find topics referenced multiple times
                else if (options.FindMultiples)
                {
                    Console.WriteLine($"\nSearching the '{options.InputDirectory}' directory and its subdirectories for " +
                        $"topics that appear more than once in one or more TOC files...\n");

                    var tocFiles = GetTocFiles(options.InputDirectory);
                    var markdownFiles = GetMarkdownFiles(options.InputDirectory, options.SearchRecursively);

                    ListPopularFiles(tocFiles, markdownFiles);
                }
                // Find orphaned images
                else if (options.FindOrphanedImages)
                {
                    var recursive = options.SearchRecursively ? "recursively " : "";
                    Console.WriteLine($"\nSearching the '{options.InputDirectory}' directory {recursive}for orphaned .png files...\n");

                    var imageFiles = GetMediaFiles(options.InputDirectory, options.SearchRecursively);

                    if (imageFiles.Count == 0)
                    {
                        Console.WriteLine("\nNo .png files were found!");
                        return;
                    }

                    ListOrphanedImages(options.InputDirectory, imageFiles, options.Delete);
                }
                // Find orphaned include-type files
                else if (options.FindOrphanedIncludes)
                {
                    var recursive = options.SearchRecursively ? "recursively " : "";
                    Console.WriteLine($"\nSearching the '{options.InputDirectory}' directory {recursive}for orphaned .md files " +
                        $"in directories named 'includes' or '_shared'.");

                    var includeFiles = GetIncludeFiles(options.InputDirectory, options.SearchRecursively);

                    if (includeFiles.Count == 0)
                    {
                        Console.WriteLine("\nNo .md files were found in any directory named 'includes' or '_shared'.");
                        return;
                    }

                    ListOrphanedIncludes(options.InputDirectory, includeFiles, options.Delete);
                }
                // Find links to topics in the central redirect file
                else if (options.FindRedirectedTopicLinks)
                {
                    Console.WriteLine($"\nSearching the '{options.InputDirectory}' directory for links to redirected topics...\n");

                    // Find the .openpublishing.redirection.json file for the directory
                    var redirectsFile = GetRedirectsFile(options.InputDirectory);

                    if (redirectsFile == null)
                    {
                        Console.WriteLine($"Could not find redirects file for directory '{options.InputDirectory}'.");
                        return;
                    }

                    // Put all the redirected files in a list
                    var redirects = GetAllRedirectedFiles(redirectsFile);
                    if (redirects is null)
                    {
                        Console.WriteLine("\nDid not find any redirects - exiting.");
                        return;
                    }

                    // Get all the markdown and YAML files.
                    var linkingFiles = GetMarkdownFiles(options.InputDirectory, options.SearchRecursively);
                    linkingFiles.AddRange(GetYAMLFiles(options.InputDirectory, options.SearchRecursively));

                    // Check all links, including in toc.yml, to files in the redirects list.
                    // Report links to redirected files and optionally replace them.
                    FindRedirectLinks(redirects, linkingFiles, options.ReplaceLinks);

                    Console.WriteLine("DONE");
                }

                stopwatch.Stop();
                Console.WriteLine($"Elapsed time: {stopwatch.Elapsed.ToHumanReadableString()}");

                // Uncomment for debugging to see console output.
                //Console.WriteLine("\nPress any key to continue.");
                //Console.ReadLine();
            }
        }

        #region Orphaned includes
        /// TODO: Improve the perf of this method using the following pseudo code:
        /// For each include file
        ///    For each markdown file
        ///       Do a RegEx search for the include file
        ///          If found, BREAK to the next include file
        private static void ListOrphanedIncludes(string inputDirectory, Dictionary<string, int> includeFiles, bool deleteOrphanedIncludes)
        {
            // Get all files that could possibly link to the include files
            var files = GetAllMarkdownFiles(inputDirectory, out var rootDirectory);

            // Gather up all the include references and increment the count for that include file in the Dictionary.
            foreach (var markdownFile in files)
            {
                foreach (var line in File.ReadAllLines(markdownFile.FullName))
                {
                    // Example include references:
                    // [!INCLUDE [DotNet Restore Note](../includes/dotnet-restore-note.md)]
                    // [!INCLUDE[DotNet Restore Note](~/includes/dotnet-restore-note.md)]
                    // [!INCLUDE [temp](../_shared/assign-to-sprint.md)]

                    // RegEx pattern to match
                    var includeLinkPattern = @"\[!INCLUDE[ ]?\[([^\]]*?)\]\(([^\)]*?)(includes|_shared)\/(.*?).md[ ]*\)[ ]*\]";

                    // There could be more than one INCLUDE reference on the line, hence the foreach loop.
                    foreach (Match match in Regex.Matches(line, includeLinkPattern, RegexOptions.IgnoreCase))
                    {
                        var relativePath = GetFilePathFromLink(match.Groups[0].Value);

                        if (relativePath != null)
                        {
                            string fullPath;

                            // Path could start with a tilde e.g. ~/includes/dotnet-restore-note.md
                            if (relativePath.StartsWith("~/"))
                            {
                                fullPath = Path.Combine(rootDirectory.FullName, relativePath.TrimStart('~', '/'));
                            }
                            else
                            {
                                // Construct the full path to the referenced INCLUDE file
                                fullPath = Path.Combine(markdownFile.DirectoryName, relativePath);
                            }

                            // Clean up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                            fullPath = Path.GetFullPath(fullPath);

                            if (fullPath != null)
                            {
                                // Increment the count for this INCLUDE file in our dictionary
                                try
                                {
                                    includeFiles[fullPath.ToLower()]++;
                                }
                                catch (KeyNotFoundException)
                                {
                                    // No need to do anything.
                                }
                            }
                        }
                    }
                }
            }

            var count = 0;

            // Print out the INCLUDE files that have zero references.
            var output = new StringBuilder();
            foreach (var includeFile in includeFiles)
            {
                if (includeFile.Value == 0)
                {
                    count++;
                    output.AppendLine(Path.GetFullPath(includeFile.Key));
                }
            }

            if (deleteOrphanedIncludes)
            {
                // Delete orphaned image files
                foreach (var includeFile in includeFiles)
                {
                    if (includeFile.Value == 0)
                    {
                        File.Delete(includeFile.Key);
                    }
                }
            }

            var deleted = deleteOrphanedIncludes ? "and deleted " : "";

            Console.WriteLine($"\nFound {deleted}{count} orphaned INCLUDE files:\n");
            Console.WriteLine(output.ToString());
            Console.WriteLine("DONE");
        }

        /// <summary>
        /// Returns a collection of *.md files in the current directory, and optionally subdirectories,
        /// if the directory name is 'includes' or '_shared'.
        /// </summary>
        /// <returns></returns>
        private static Dictionary<string, int> GetIncludeFiles(string inputDirectory, bool searchRecursively)
        {
            var dir = new DirectoryInfo(inputDirectory);

            var searchOption = searchRecursively ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            var includeFiles = new Dictionary<string, int>();

            if (string.Compare(dir.Name, "includes", true) == 0
                || string.Compare(dir.Name, "_shared", true) == 0)
            {
                // This is a folder that is likely to contain "include"-type files, i.e. files that aren't in the TOC.

                foreach (var file in dir.EnumerateFiles("*.md"))
                {
                    includeFiles.Add(file.FullName.ToLower(), 0);
                }
            }

            if (searchOption == SearchOption.AllDirectories)
            {
                foreach (var subDirectory in dir.EnumerateDirectories("*", SearchOption.AllDirectories))
                {
                    if (string.Compare(subDirectory.Name, "includes", true) == 0
                        || string.Compare(subDirectory.Name, "_shared", true) == 0)
                    {
                        // This is a folder that is likely to contain "include"-type files, i.e. files that aren't in the TOC.

                        foreach (var file in subDirectory.EnumerateFiles("*.md"))
                        {
                            includeFiles.Add(file.FullName.ToLower(), 0);
                        }
                    }
                }
            }

            return includeFiles;
        }
        #endregion

        #region Orphaned images
        /// <summary>
        /// If any of the input image files are not
        /// referenced from a markdown (.md) file anywhere in the docset, including up the directory 
        /// until the docfx.json file is found, the file path of those files is written to the console.
        /// </summary>
        /// TODO: Improve the perf of this method using the following pseudo code:
        /// For each image
        ///    For each markdown file
        ///       Do a RegEx search for the image
        ///          If found, BREAK to the next image
        private static void ListOrphanedImages(string inputDirectory, Dictionary<string, int> imageFiles, bool deleteOrphanedImages)
        {
            var files = GetAllMarkdownFiles(inputDirectory, out var rootDirectory);

            void TryIncrementFile(string key, Dictionary<string, int> fileMap)
            {
                if (fileMap.ContainsKey(key))
                {
                    ++fileMap[key];
                }
            }

            // Gather up all the image references and increment the count for that image in the Dictionary.
            foreach (var markdownFile in files)
            {
                foreach (var line in File.ReadAllLines(markdownFile.FullName))
                {
                    // Match []() image references where the path to the image file includes the name of the input media directory.
                    // This includes links that don't start with ! for images that are referenced as a hyperlink
                    // instead of an image to display.

                    // RegEx pattern to match
                    var mdImageRegEx = @"\]\(([^\)])*\.png([^\)])*\)";

                    // There could be more than one image reference on the line, hence the foreach loop.
                    foreach (Match match in Regex.Matches(line, mdImageRegEx, RegexOptions.IgnoreCase))
                    {
                        var relativePath = GetFilePathFromLink(match.Groups[0].Value);

                        if (relativePath != null)
                        {
                            // Construct the full path to the referenced image file
                            string fullPath = null;
                            try
                            {
                                // Path could start with a tilde e.g. ~/media/pic1.png
                                if (relativePath.StartsWith("~/"))
                                {
                                    fullPath = Path.Combine(rootDirectory.FullName, relativePath.TrimStart('~', '/'));
                                }
                                else
                                {
                                    fullPath = Path.Combine(markdownFile.DirectoryName, relativePath);
                                }
                            }
                            catch (ArgumentException)
                            {
                                Console.WriteLine($"Possible bad image link '{line}' in file '{markdownFile.FullName}'.\n");
                                break;
                            }

                            // This cleans up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                            try
                            {
                                fullPath = Path.GetFullPath(fullPath);
                            }
                            catch (ArgumentException)
                            {
                                Console.WriteLine($"Possible bad image link '{line}' in file '{markdownFile.FullName}'.\n");
                                break;
                            }

                            if (fullPath != null)
                            {
                                TryIncrementFile(fullPath, imageFiles);
                            }
                        }
                    }

                    // Match "img src=" references
                    // Example: <img data-hoverimage="./images/getstarted.svg" src="./images/getstarted.png" alt="Get started icon" />

                    var htmlImageRegEx = @"<img([^>])*src([^>])*>";
                    foreach (Match match in Regex.Matches(line, htmlImageRegEx, RegexOptions.IgnoreCase))
                    {
                        var relativePath = GetFilePathFromLink(match.Groups[0].Value);

                        if (relativePath != null)
                        {
                            // Construct the full path to the referenced image file
                            var fullPath = Path.Combine(markdownFile.DirectoryName, relativePath);

                            // This cleans up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                            fullPath = TryGetFullPath(fullPath);

                            if (fullPath != null)
                            {
                                TryIncrementFile(fullPath, imageFiles);
                            }
                        }
                    }

                    // Match reference-style image links
                    // Example: [0]: ../../media/vs-acr-provisioning-dialog-2019.png

                    var referenceLinkRegEx = @"\[(.)*?\]:(.)*?.png";
                    foreach (Match match in Regex.Matches(line, referenceLinkRegEx, RegexOptions.IgnoreCase))
                    {
                        var relativePath = GetFilePathFromLink(match.Groups[0].Value);

                        if (relativePath != null)
                        {
                            // Construct the full path to the referenced image file
                            var fullPath = Path.Combine(markdownFile.DirectoryName, relativePath);

                            // This cleans up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                            fullPath = TryGetFullPath(fullPath);

                            if (fullPath != null)
                            {
                                TryIncrementFile(fullPath, imageFiles);
                            }
                        }
                    }
                }
            }

            var count = 0;

            // Print out the image files with zero references.
            var output = new StringBuilder();
            foreach (var image in imageFiles)
            {
                if (image.Value == 0)
                {
                    count++;
                    output.AppendLine(Path.GetFullPath(image.Key));
                }
            }

            if (deleteOrphanedImages)
            {
                // Delete orphaned image files
                foreach (var image in imageFiles)
                {
                    if (image.Value == 0)
                    {
                        try
                        {
                            File.Delete(image.Key);
                        }
                        catch (PathTooLongException)
                        {
                            output.AppendLine($"Unable to delete {image.Key} because its path is too long.");
                        }
                    }
                }
            }

            var deleted = deleteOrphanedImages ? "and deleted " : "";

            Console.WriteLine($"\nFound {deleted}{count} orphaned .png files:\n");
            Console.WriteLine(output.ToString());
            Console.WriteLine("DONE");
        }

        /// <summary>
        /// Returns a dictionary of all .png files in the directory.
        /// The search includes the specified directory and (optionally) all its subdirectories.
        /// </summary>
        private static Dictionary<string, int> GetMediaFiles(string mediaDirectory, bool searchRecursively = true)
        {
            var dir = new DirectoryInfo(mediaDirectory);
            var searchOption = searchRecursively ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var mediaFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in dir.EnumerateFiles("*.png", searchOption))
            {
                mediaFiles.Add(file.FullName.ToLower(), 0);
            }

            return mediaFiles;
        }
        #endregion

        #region Orphaned topics
        /// <summary>
        /// Lists the files that aren't in a TOC.
        /// Optionally, only list files that don't have a redirect_url metadata tag.
        /// </summary>
        private static void ListOrphanedTopics(List<FileInfo> tocFiles, List<FileInfo> markdownFiles, bool deleteOrphanedTopics)
        {
            var countNotFound = 0;
            var countDeleted = 0;

            var output = new StringBuilder("\nTopic details:\n\n");

            foreach (var markdownFile in markdownFiles)
            {
                var found = false;

                // If the file is in the Includes directory, or the file is a TOC or index file, ignore it
                if (markdownFile.FullName.Contains("\\includes\\")
                    || markdownFile.FullName.Contains("\\_shared\\")
                    || string.Compare(markdownFile.Name, "TOC.md", true) == 0
                    || string.Compare(markdownFile.Name, "index.md", true) == 0)
                    continue;

                foreach (var tocFile in tocFiles)
                {
                    if (!IsFileLinkedFromTocFile(markdownFile, tocFile))
                    {
                        continue;
                    }

                    found = true;
                    break;
                }

                if (!found)
                {
                    ++ countNotFound;

                    // Try to delete the file if the option is set.
                    if (deleteOrphanedTopics)
                    {
                        var isLinked = false;
                        var referencedFile = "";
                        foreach (var otherMarkdownFile in markdownFiles.Where(file => file != markdownFile))
                        {
                            if (!IsFileLinkedInFile(markdownFile, otherMarkdownFile))
                            {
                                continue;
                            }

                            referencedFile = otherMarkdownFile.FullName;
                            isLinked = true;
                            break;
                        }

                        if (isLinked)
                        {
                            output.AppendLine($"Unable to delete '{markdownFile.FullName}'");
                            output.AppendLine($"    It is referenced in '{referencedFile}'");
                        }
                        else
                        {
                            ++ countDeleted;
                            output.AppendLine($"Deleting '{markdownFile.FullName}'.");

                            File.Delete(markdownFile.FullName);
                        }
                    }
                }
            }

            var deletedMessage = deleteOrphanedTopics ? $"Deleted {countDeleted} of these files." : "";
            output.AppendLine($"\nFound {countNotFound} .md files that aren't referenced in a TOC. {deletedMessage}\n");
            Console.Write(output.ToString());
        }

        private static bool IsFileLinkedFromTocFile(FileInfo linkedFile, FileInfo tocFile)
        {
            var text = File.ReadAllText(tocFile.FullName);
            var linkRegEx = tocFile.Extension.ToLower() == ".yml" ? @"href: (.)*" + linkedFile.Name : @"]\((?!http)([^\)])*" + linkedFile.Name + @"\)";

            // For each link that contains the file name...
            foreach (Match match in Regex.Matches(text, linkRegEx, RegexOptions.IgnoreCase))
            {
                // Get the file-relative path to the linked file.
                var relativePath = GetFilePathFromLink(match.Groups[0].Value);

                if (relativePath != null)
                {
                    // Construct the full path to the referenced file
                    var fullPath = TryGetFullPathFromDirectory(tocFile.DirectoryName, relativePath);
                    if (fullPath != null)
                    {
                        // See if our constructed path matches the actual file we think it is
                        if (string.Compare(fullPath, linkedFile.FullName, true) == 0)
                        {
                            return true;
                        }
                        else
                        {
                            // If we get here, the file name matched but the full path did not.
                        }
                    }
                }
            }

            // We did not find this file linked in the specified file.
            return false;
        }
        #endregion

        #region Redirected files
        private class Redirect
        {
            [JsonProperty(PropertyName = "source_path")]
            public string SourcePath { get; set; }

            [JsonProperty(PropertyName = "redirect_url")]
            public string RedirectUrl { get; set; }

            [JsonProperty(PropertyName = "redirect_document_id")]
            public bool RedirectDocumentId { get; set; }
        }

        private static FileInfo GetRedirectsFile(string inputDirectory)
        {
            var dir = new DirectoryInfo(inputDirectory);

            try
            {
                var files = dir.GetFiles(".openpublishing.redirection.json", SearchOption.TopDirectoryOnly);
                while (dir.GetFiles(".openpublishing.redirection.json", SearchOption.TopDirectoryOnly).Length == 0)
                {
                    dir = dir.Parent;

                    // Loop exit condition.
                    if (dir == dir.Root)
                        return null;
                }

                return dir.GetFiles(".openpublishing.redirection.json", SearchOption.TopDirectoryOnly)[0];
            }
            catch (DirectoryNotFoundException)
            {
                Console.WriteLine($"Could not find directory {dir.FullName}");
                throw;
            }
        }

        private static List<Redirect> LoadRedirectJson(FileInfo redirectsFile)
        {
            using (var reader = new StreamReader(redirectsFile.FullName))
            {
                var json = reader.ReadToEnd();

                // Trim the string so we're just left with an array of redirect objects
                json = json.Trim();
                json = json.Substring(json.IndexOf('['));
                json = json.TrimEnd('}');

                try
                {
                    return JsonConvert.DeserializeObject<List<Redirect>>(json);
                }
                catch (JsonReaderException e)
                {
                    Console.WriteLine($"Caught exception while reading JSON file: {e.Message}");
                    return null;
                }
            }
        }

        private static List<Redirect> GetAllRedirectedFiles(FileInfo redirectsFile)
        {
            var redirects = LoadRedirectJson(redirectsFile);

            if (redirects is null)
            {
                return null;
            }

            foreach (var redirect in redirects)
            {
                if (redirect.SourcePath != null)
                {
                    // Construct the full path to the redirected file
                    var fullPath = Path.Combine(redirectsFile.DirectoryName, redirect.SourcePath);

                    // This cleans up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                    fullPath = Path.GetFullPath(fullPath);

                    redirect.SourcePath = fullPath;
                }
            }

            return redirects;
        }

        private static void FindRedirectLinks(List<Redirect> redirects, List<FileInfo> linkingFiles, bool replaceLinks)
        {
            var redirectLookup = Enumerable.ToDictionary<Redirect, string>(redirects, r => r.SourcePath);

            // For each file...
            foreach (var linkingFile in linkingFiles)
            {
                var foundOldLink = false;
                var output = new StringBuilder($"FILE '{linkingFile.FullName}' contains the following link(s) to redirected files:\n\n");

                var text = File.ReadAllText(linkingFile.FullName);

                var linkRegEx = linkingFile.Extension.ToLower() == ".yml" ? @"href: (.)*\.md" : @"]\((?!http)([^\)])*\.md\)";

                // For each link in the file...
                foreach (Match match in Regex.Matches(text, linkRegEx, RegexOptions.IgnoreCase))
                {
                    // Get the file-relative path to the linked file.
                    var relativePath = GetFilePathFromLink(match.Groups[0].Value);

                    if (relativePath is null)
                    {
                        Console.WriteLine($"Found a possibly malformed link '{match.Groups[0].Value}' in '{linkingFile.FullName}'.\n");
                        break;
                    }

                    // Construct the full path to the linked file.
                    var fullPath = Path.Combine(linkingFile.DirectoryName, relativePath);
                    // Clean up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                    try
                    {
                        fullPath = Path.GetFullPath(fullPath);
                    }
                    catch (NotSupportedException)
                    {
                        Console.WriteLine($"Found a possibly malformed link '{match.Groups[0].Value}' in '{linkingFile.FullName}'.\n");
                        break;
                    }

                    if (fullPath != null)
                    {
                        // See if our constructed path matches a source file in the dictionary of redirects.
                        if (redirectLookup.ContainsKey(fullPath))
                        {
                            foundOldLink = true;
                            output.AppendLine($"'{relativePath}'");

                            // Replace the link if requested.
                            if (replaceLinks)
                            {
                                var redirectURL = redirectLookup[fullPath].RedirectUrl;

                                output.AppendLine($"REPLACING '{relativePath}' with '{redirectURL}'.");

                                var newText = text.Replace(relativePath, redirectURL);
                                File.WriteAllText(linkingFile.FullName, newText);
                            }
                        }

                    }
                }

                if (foundOldLink)
                {
                    Console.WriteLine(output.ToString());
                }
            }
        }
        #endregion

        #region Popular files
        /// <summary>
        /// Finds topics that appear more than once, either in one TOC.md file, or multiple TOC.md files.
        /// </summary>
        private static void ListPopularFiles(List<FileInfo> tocFiles, List<FileInfo> markdownFiles)
        {
            var found = false;
            var output = new StringBuilder("The following files appear in more than one TOC file:\n\n");

            // Keep a hash table of each topic path with the number of times it's referenced
            var topics = markdownFiles.ToDictionary<FileInfo, string, int>(mf => mf.FullName, mf => 0);

            foreach (var markdownFile in markdownFiles)
            {
                // If the file is in the Includes directory, ignore it
                if (markdownFile.FullName.Contains("\\includes\\"))
                    continue;

                foreach (var tocFile in tocFiles)
                {
                    if (IsFileLinkedInFile(markdownFile, tocFile))
                    {
                        topics[markdownFile.FullName]++;
                    }
                }
            }

            // Now spit out the topics that appear more than once.
            foreach (var topic in topics)
            {
                if (topic.Value > 1)
                {
                    found = true;
                    output.AppendLine(topic.Key);
                }
            }

            // Only write the StringBuilder to the console if we found a topic referenced from more than one TOC file.
            if (found)
            {
                Console.Write(output.ToString());
            }
        }
        #endregion

        #region Generic helper methods
        /// <summary>
        /// Checks if the specified file path is referenced in the specified file.
        /// </summary>
        private static bool IsFileLinkedInFile(FileInfo linkedFile, FileInfo linkingFile)
        {
            if (!File.Exists(linkingFile.FullName))
            {
                return false;
            }

            foreach (var path in 
                File.ReadAllLines(linkingFile.FullName)
                    .Where(str => !string.IsNullOrWhiteSpace(str) && str.Contains(linkedFile.Name))
                    .SelectMany(FindAllLinksInLine)
                    .Where(filePath => !string.IsNullOrWhiteSpace(filePath)))
            {
                var fullPath = TryGetFullPathFromDirectory(linkingFile.DirectoryName, path);
                if (fullPath != null)
                {
                    // See if our constructed path matches the actual file we think it is
                    if (string.Compare(fullPath, linkedFile.FullName, true) == 0)
                    {
                        return true;
                    }
                    else
                    {
                        // If we get here, the file name matched but the full path did not.
                    }
                }
            }

            // We did not find this file linked in the specified file.
            return false;
        }

        private static IEnumerable<string> FindAllLinksInLine(string line)
        {
            // Find all potential link matches in a single line
            // - **Helper library**: [Create a Content Moderator client for use in other samples](https://github.com/Azure-Samples/cognitive-services-dotnet-sdk-samples/blob/master/ContentModerator/ModeratorHelper/Clients.cs). See [quickstart](content-moderator-helper-quickstart-dotnet.md).
            // Returns:
            // [ 
            //     "https://github.com/Azure-Samples/cognitive-services-dotnet-sdk-samples/blob/master/ContentModerator/ModeratorHelper/Clients.cs",
            //     "content-moderator-helper-quickstart-dotnet.md" 
            // ]

            return IterateMatches(
                Regex.Matches(line, @"(?<=\().+?(?=\))"), // Markdown links
                Regex.Matches(line, @"<img[^>]*?src\s*=\s*[""']?([^'"" >]+?)[ '""][^>]*?>"), // src attribute in img tags
                Regex.Matches(line, "href:.+?(.*)")); // href in yml/yaml files

            IEnumerable<string> IterateMatches(params MatchCollection[] matches)
            {
                foreach (var match in matches.SelectMany(collection => collection.Cast<Match>()))
                {
                    var value = match.Value;
                    if (string.IsNullOrWhiteSpace(value) || value.Contains("http"))
                    {
                        yield return null;
                    }

                    yield return value;
                }
            }
        }

        /// <summary>
        /// Returns the file path from the specified text that contains 
        /// either the pattern "[text](file path)", "href:", or "img src=".
        /// Returns null if the file is in a different repo or is an http URL.
        /// </summary>
        private static string GetFilePathFromLink(string text)
        {
            // Example image references:
            // ![Auto hide](../ide/media/vs2015_auto_hide.png)
            // ![Unit Test Explorer showing Run All button](../test/media/unittestexplorer-beta-.png "UnitTestExplorer(beta)")
            // ![link to video](../data-tools/media/playvideo.gif "PlayVideo")For a video version of this topic, see...
            // <img src="../data-tools/media/logo_azure-datalake.svg" alt=""
            // The Light Bulb icon ![Small Light Bulb Icon](media/vs2015_lightbulbsmall.png "VS2017_LightBulbSmall"),

            // but not:
            // <![CDATA[

            // Example .md file reference in a TOC:
            // ### [Managing External Tools](ide/managing-external-tools.md)

            if (text.Contains("]("))
            {
                text = text.Substring(text.IndexOf("](") + 2);

                if (text.StartsWith("/") || text.StartsWith("http"))
                {
                    // The file is in a different repo, so ignore it.
                    return null;
                }

                // Look for the closing parenthesis.
                string relativePath;
                try
                {
                    relativePath = text.Substring(0, text.IndexOf(')'));
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Image link is likely badly formatted.
                    Console.WriteLine($"Possible malformed image link in '{text}'.\n");
                    return null;
                }

                // Trim any whitespace on the beginning and end
                relativePath = relativePath.Trim();

                // If there is a whitespace character in the string, truncate it there.
                var index = relativePath.IndexOf(' ');
                if (index > 0)
                {
                    relativePath = relativePath.Substring(0, index);
                }

                // Handle links with a # sign, e.g. media/how-to-use-lightboxes/xamarin.png#lightbox.
                var hashIndex = relativePath.LastIndexOf('#');
                if (hashIndex > 0)
                {
                    relativePath = relativePath.Substring(0, hashIndex);
                }

                return relativePath;
            }
            else if (text.Contains("]:"))
            {
                text = text.Substring(text.IndexOf("]:") + 2).Trim();
                return text;
            }
            else if (text.Contains("href:"))
            {
                // e.g. href: ../ide/quickstart-python.md
                // e.g. href: debugger/getting-started-with-the-debugger.md?context=visualstudio/default&contextView=vs-2017
                text = text.Substring(text.IndexOf("href:") + 5).Trim();

                // Handle contextual TOC links and others that have a ? in them
                if (text.IndexOf('?') >= 0)
                {
                    text = text.Substring(0, text.IndexOf('?'));
                }

                return text;
            }
            else if (text.Contains("src="))
            {
                text = text.Substring(text.IndexOf("src=") + 4);

                // Remove opening quotation marks, if present.
                text = text.TrimStart('"');

                if (text.StartsWith("/") || text.StartsWith("http"))
                {
                    // The file is in a different repo, so ignore it.
                    return null;
                }

                // Check that the path is valid, i.e. it starts with a letter or a '.'.
                // RegEx pattern to match
                var imageLinkPattern = @"^(\w|\.).*";

                if (Regex.Matches(text, imageLinkPattern).Count > 0)
                {
                    try
                    {
                        return text.Substring(0, text.IndexOf('"'));
                    }
                    catch (ArgumentException)
                    {
                        Console.WriteLine($"Caught ArgumentException while extracting the image path from the following text: {text}\n");
                        return null;
                    }
                }
                else
                {
                    // Unrecognizable file path.
                    Console.WriteLine($"Unrecognizable file path (ignoring this image link): {text}\n");
                    return null;
                }
            }
            else
            {
                throw new ArgumentException($"Argument 'line' does not contain an expected link pattern.");
            }
        }

        /// <summary>
        /// Gets all *.md files recursively, starting in the specified directory.
        /// </summary>
        private static List<FileInfo> GetMarkdownFiles(string directoryPath, bool searchRecursively)
        {
            var dir = new DirectoryInfo(directoryPath);
            var searchOption = searchRecursively ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            return dir.EnumerateFiles("*.md", searchOption).ToList();
        }

        /// <summary>
        /// Gets all *.yml files recursively, starting in the specified directory.
        /// </summary>
        private static List<FileInfo> GetYAMLFiles(string directoryPath, bool searchRecursively)
        {
            var dir = new DirectoryInfo(directoryPath);
            var searchOption = searchRecursively ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            return dir.EnumerateFiles("*.yml", searchOption).ToList();
        }

        /// <summary>
        /// Gets all *.md files recursively, starting in the ancestor directory that contains docfx.json.
        /// </summary>
        private static List<FileInfo> GetAllMarkdownFiles(string directoryPath, out DirectoryInfo rootDirectory)
        {
            // Look further up the path until we find docfx.json
            rootDirectory = GetDocFxDirectory(new DirectoryInfo(directoryPath));

            return rootDirectory.EnumerateFiles("*.md", SearchOption.AllDirectories).ToList();
        }

        /// <summary>
        /// Gets all TOC.* files recursively, starting in the specified directory if it contains "docfx.json" file.
        /// Otherwise it looks up the directory path until it finds a "docfx.json" file. Then it starts the recursive search
        /// for TOC.* files from that directory.
        /// </summary>
        private static List<FileInfo> GetTocFiles(string directoryPath)
        {
            var dir = new DirectoryInfo(directoryPath);

            // Look further up the path until we find docfx.json
            dir = GetDocFxDirectory(dir);

            return dir.EnumerateFiles("TOC.*", SearchOption.AllDirectories).ToList();
        }

        /// <summary>
        /// Returns the specified directory if it contains a file named "docfx.json".
        /// Otherwise returns the nearest parent directory that contains a file named "docfx.json".
        /// </summary>
        private static DirectoryInfo GetDocFxDirectory(DirectoryInfo dir)
        {
            try
            {
                while (dir.GetFiles("docfx.json", SearchOption.TopDirectoryOnly).Length == 0)
                {
                    dir = dir.Parent;

                    if (dir == dir.Root)
                        throw new Exception("Could not find docfx.json file in directory or parent.");
                }
            }
            catch (DirectoryNotFoundException)
            {
                Console.WriteLine($"Could not find directory {dir.FullName}");
                throw;
            }

            return dir;
        }

        private static string TryGetFullPathFromDirectory(string directory, string path)
        {
            try
            {
                var combinedPath = Path.Combine(directory, path);
                if (string.IsNullOrWhiteSpace(combinedPath))
                {
                    return null;
                }

                return TryGetFullPath(combinedPath);
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"Unable to get path (perhaps illegal characters): {path}");
                return null;
            }
        }

        private static string TryGetFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (PathTooLongException)
            {
                Console.WriteLine($"Unable to get path because it's too long: {path}");
                return null;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"Unable to get path (perhaps illegal characters): {path}");
                return null;
            }
            catch (NotSupportedException)
            {
                Console.WriteLine($"Unable to get path (perhaps format not supported): {path}");
                return null;
            }
        }
        #endregion
    }
}