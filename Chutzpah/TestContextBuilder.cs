﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Chutzpah.FrameworkDefinitions;
using Chutzpah.Models;
using Chutzpah.Wrappers;

namespace Chutzpah
{

    public class TestContextBuilder : ITestContextBuilder
    {
        private readonly IFileSystemWrapper fileSystem;
        private readonly IFileProbe fileProbe;
        private IEnumerable<IFrameworkDefinition> frameworkDefinitions;

        private readonly Regex JsReferencePathRegex = new Regex(@"^\s*///\s*<\s*reference\s+path\s*=\s*[""""'](?<Path>[^""""<>|]+)[""""']\s*/>",
                                                              RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly Regex HtmlReferencePathRegex = new Regex(@"^\s*<\s*script\s*.*?src\s*=\s*[""""'](?<Path>[^""""<>|]+)[""""'].*?>", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public TestContextBuilder(IFileSystemWrapper fileSystem, IFileProbe fileProbe, IEnumerable<IFrameworkDefinition> frameworkDefinitions)
        {
            this.fileSystem = fileSystem;
            this.fileProbe = fileProbe;
            this.frameworkDefinitions = frameworkDefinitions;
        }

        public TestContext BuildContext(string file)
        {
            return this.BuildContext(file, null);
        }

        public TestContext BuildContext(string file, string stagingFolder)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new ArgumentNullException("file");
            }

            var pathInfo = fileProbe.GetPathInfo(file);
            var fileKind = pathInfo.Type;
            var filePath = pathInfo.FullPath;

            if (fileKind != PathType.JavaScript && fileKind != PathType.Html)
            {
                throw new ArgumentException("Expecting a .js or .html file");
            }

            stagingFolder = string.IsNullOrEmpty(stagingFolder) ? fileSystem.GetTemporaryFolder() : stagingFolder;

            if (filePath == null)
            {
                throw new FileNotFoundException("Unable to find file: " + file);
            }

            var testFileName = Path.GetFileName(file);
            var testFileText = fileSystem.GetText(filePath);

            IFrameworkDefinition definition;

            if (this.TryDetectFramework(testFileText, out definition))
            {
                if (!fileSystem.FolderExists(stagingFolder))
                {
                    fileSystem.CreateDirectory(stagingFolder);
                }

                var referencedFiles = GetReferencedFiles(definition, fileKind, testFileText, filePath, stagingFolder);
                var fixtureContent = "";

                if (fileKind == PathType.JavaScript)
                {
                    var stagedFilePath = Path.Combine(stagingFolder, testFileName);
                    referencedFiles.Add(new ReferencedFile { Path = filePath, StagedPath = stagedFilePath, IsLocal = true, IsFileUnderTest = true });
                }
                else if (fileKind == PathType.Html)
                {
                    fixtureContent = definition.GetFixtureContent(testFileText);
                }

                this.CopyReferencedFiles(referencedFiles, definition);

                foreach (var item in definition.FileDependencies)
                {
                    var itemPath = Path.Combine(stagingFolder, item);
                    this.CreateIfDoesNotExist(itemPath, "Chutzpah.TestFiles." + item);
                }

                var testHtmlFilePath = CreateTestHarness(definition, stagingFolder, referencedFiles, fixtureContent);

                return new TestContext
                {
                    InputTestFile = filePath,
                    TestHarnessPath = testHtmlFilePath,
                    ReferencedJavaScriptFiles = referencedFiles,
                    TestRunner = definition.TestRunner
                };
            }

            return null;
        }

        public bool TryBuildContext(string file, out TestContext context)
        {
            return this.TryBuildContext(file, null, out context);
        }

        public bool TryBuildContext(string file, string stagingFolder, out TestContext context)
        {
            context = this.BuildContext(file, stagingFolder);
            return context != null;
        }

        private bool TryDetectFramework(string content, out IFrameworkDefinition definition)
        {
            definition = this.frameworkDefinitions.FirstOrDefault(x => x.FileUsesFramework(content, false));

            if (definition == null)
            {
                definition = this.frameworkDefinitions.FirstOrDefault(x => x.FileUsesFramework(content, true));
            }

            return definition != null;
        }

        private string CreateTestHarness(IFrameworkDefinition definition, string stagingFolder, IEnumerable<ReferencedFile> referencedFiles, string fixtureContent)
        {
            var testHtmlFilePath = Path.Combine(stagingFolder, "test.html");
            var testHtmlTemplate = EmbeddedManifestResourceReader.GetEmbeddedResoureText<TestRunner>("Chutzpah.TestFiles." + definition.TestHarness);
            string testHtmlText = FillTestHtmlTemplate(testHtmlTemplate, referencedFiles, fixtureContent);
            fileSystem.Save(testHtmlFilePath, testHtmlText);
            return testHtmlFilePath;
        }

        private void CreateIfDoesNotExist(string filePath, string embeddedPath)
        {
            if (!fileSystem.FileExists(filePath))
            {
                using (var stream = EmbeddedManifestResourceReader.GetEmbeddedResoureStream<TestRunner>(embeddedPath))
                {
                    fileSystem.Save(filePath, stream);
                }
            }
        }

        private IList<ReferencedFile> GetReferencedFiles(IFrameworkDefinition definition, PathType testFileType, string testFileText, string testFilePath, string stagingFolder)
        {
            var regex = testFileType == PathType.JavaScript ? JsReferencePathRegex : HtmlReferencePathRegex;
            var files = new List<ReferencedFile>();
            foreach (Match match in regex.Matches(testFileText))
            {
                if (match.Success)
                {
                    string referencePath = match.Groups["Path"].Value;
                    Uri referenceUri = new Uri(referencePath, UriKind.RelativeOrAbsolute);
                    var referenceFileName = Path.GetFileName(referencePath);

                    // Don't copy over test runner, since we use our own.
                    if (definition.ReferenceIsDependency(referenceFileName))
                    {
                        continue;
                    }

                    if (!referenceUri.IsAbsoluteUri || referenceUri.IsFile)
                    {
                        string relativeReferencePath = Path.Combine(Path.GetDirectoryName(testFilePath), referencePath);
                        var absolutePath = fileProbe.FindFilePath(relativeReferencePath);
                        if (absolutePath != null)
                        {
                            var uniqueFileName = MakeUniqueIfNeeded(referenceFileName, stagingFolder);
                            var stagedPath = Path.Combine(stagingFolder, uniqueFileName);
                            files.Add(new ReferencedFile { Path = absolutePath, StagedPath = stagedPath, IsLocal = true });
                        }
                    }
                    else if (referenceUri.IsAbsoluteUri)
                    {
                        files.Add(new ReferencedFile { Path = referencePath, StagedPath = referencePath, IsLocal = false });
                    }
                }
            }
            return files;
        }

        private void CopyReferencedFiles(IEnumerable<ReferencedFile> referencedFiles, IFrameworkDefinition definition)
        {
            foreach (var referencedFile in referencedFiles)
            {
                if (referencedFile.IsLocal)
                {
                    fileSystem.CopyFile(referencedFile.Path, referencedFile.StagedPath);
                    fileSystem.SetFileAttributes(referencedFile.StagedPath, FileAttributes.Normal);
                    definition.Process(referencedFile);
                }
            }
        }

        private static string FillTestHtmlTemplate(string testHtmlTemplate, IEnumerable<ReferencedFile> referencedFiles, string fixtureContent)
        {
            var referenceReplacement = new StringBuilder();
            foreach (ReferencedFile referencedFile in referencedFiles)
            {
                var referencePath = referencedFile.IsLocal ? Path.GetFileName(referencedFile.StagedPath) : referencedFile.StagedPath;
                referenceReplacement.AppendLine(GetScriptStatement(referencePath));
            }

            testHtmlTemplate = testHtmlTemplate.Replace("@@ReferencedFiles@@", referenceReplacement.ToString());
            testHtmlTemplate = testHtmlTemplate.Replace("@@FixtureContent@@", fixtureContent);


            return testHtmlTemplate;
        }

        public static string GetScriptStatement(string path)
        {
            const string format = @"<script type=""text/javascript"" src=""{0}""></script>";
            return string.Format(format, path);
        }

        private string MakeUniqueIfNeeded(string fileName, string directoryPath)
        {
            var filePath = Path.Combine(directoryPath, fileName);
            if (fileSystem.FileExists(filePath))
            {
                var randomFileName = fileSystem.GetRandomFileName();
                return string.Format("{0}_{1}", randomFileName, fileName);
            }

            return fileName;
        }
    }
}