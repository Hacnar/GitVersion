using System;
using System.IO;
using System.Text;
using GitTools.Testing;
using GitVersion;
using GitVersion.BuildAgents;
using GitVersion.Configuration;
using GitVersion.Logging;
using GitVersion.Model.Configuration;
using GitVersion.VersionCalculation.Cache;
using GitVersionCore.Tests.Helpers;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Shouldly;
using Environment = System.Environment;

namespace GitVersionCore.Tests
{
    [TestFixture]
    [Parallelizable(ParallelScope.None)]
    public class GitVersionExecutorTests : TestBase
    {
        private IFileSystem fileSystem;
        private ILog log;
        private IGitVersionCache gitVersionCache;
        private IGitPreparer gitPreparer;
        private IServiceProvider sp;

        [Test]
        public void CacheKeySameAfterReNormalizing()
        {
            using var fixture = new EmptyRepositoryFixture();
            var targetUrl = "https://github.com/GitTools/GitVersion.git";
            var targetBranch = "refs/head/master";

            var gitVersionOptions = new GitVersionOptions
            {
                RepositoryInfo = { TargetUrl = targetUrl, TargetBranch = targetBranch },
                WorkingDirectory = fixture.RepositoryPath,
                Settings = { NoNormalize = false }
            };

            var environment = new TestEnvironment();
            environment.SetEnvironmentVariable(AzurePipelines.EnvironmentVariableName, "true");

            sp = GetServiceProvider(gitVersionOptions, environment: environment);

            var preparer = sp.GetService<IGitPreparer>();

            preparer.Prepare();
            var cacheKeyFactory = sp.GetService<IGitVersionCacheKeyFactory>();
            var cacheKey1 = cacheKeyFactory.Create(null);
            preparer.Prepare();

            var cacheKey2 = cacheKeyFactory.Create(null);

            cacheKey2.Value.ShouldBe(cacheKey1.Value);
        }

        [Test]
        public void GitPreparerShouldNotFailWhenTargetPathNotInitialized()
        {
            var targetUrl = "https://github.com/GitTools/GitVersion.git";

            var gitVersionOptions = new GitVersionOptions
            {
                RepositoryInfo = { TargetUrl = targetUrl },
                WorkingDirectory = null
            };
            Should.NotThrow(() =>
            {
                sp = GetServiceProvider(gitVersionOptions);

                sp.GetService<IGitPreparer>();
            });
        }

        [Test]
        [Category("NoMono")]
        [Description("LibGit2Sharp fails here when running under Mono")]
        public void CacheKeyForWorktree()
        {
            using var fixture = new EmptyRepositoryFixture();
            fixture.Repository.MakeACommit();
            var worktreePath = Path.Combine(Directory.GetParent(fixture.RepositoryPath).FullName, Guid.NewGuid().ToString());
            try
            {
                // create a branch and a new worktree for it
                var repo = new Repository(fixture.RepositoryPath);
                repo.Worktrees.Add("worktree", worktreePath, false);

                var targetUrl = "https://github.com/GitTools/GitVersion.git";

                var gitVersionOptions = new GitVersionOptions
                {
                    RepositoryInfo = { TargetUrl = targetUrl, TargetBranch = "master" },
                    WorkingDirectory = worktreePath
                };

                sp = GetServiceProvider(gitVersionOptions);

                var preparer = sp.GetService<IGitPreparer>();
                preparer.Prepare();
                var cacheKey = sp.GetService<IGitVersionCacheKeyFactory>().Create(null);
                cacheKey.Value.ShouldNotBeEmpty();
            }
            finally
            {
                DirectoryHelper.DeleteDirectory(worktreePath);
            }
        }

        [Test]
        public void CacheFileExistsOnDisk()
        {
            const string versionCacheFileContent = @"
        Major: 4
        Minor: 10
        Patch: 3
        PreReleaseTag: test.19
        PreReleaseTagWithDash: -test.19
        PreReleaseLabel: test
        PreReleaseNumber: 19
        WeightedPreReleaseNumber: 19
        BuildMetaData:
        BuildMetaDataPadded:
        FullBuildMetaData: Branch.feature/test.Sha.dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        MajorMinorPatch: 4.10.3
        SemVer: 4.10.3-test.19
        LegacySemVer: 4.10.3-test19
        LegacySemVerPadded: 4.10.3-test0019
        AssemblySemVer: 4.10.3.0
        AssemblySemFileVer: 4.10.3.0
        FullSemVer: 4.10.3-test.19
        InformationalVersion: 4.10.3-test.19+Branch.feature/test.Sha.dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        BranchName: feature/test
        EscapedBranchName: feature-test
        Sha: dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        ShortSha: dd2a29af
        NuGetVersionV2: 4.10.3-test0019
        NuGetVersion: 4.10.3-test0019
        NuGetPreReleaseTagV2: test0019
        NuGetPreReleaseTag: test0019
        VersionSourceSha: 4.10.2
        CommitsSinceVersionSource: 19
        CommitsSinceVersionSourcePadded: 0019
        CommitDate: 2015-11-10
        ";

            var stringBuilder = new StringBuilder();
            void Action(string s) => stringBuilder.AppendLine(s);

            var logAppender = new TestLogAppender(Action);
            log = new Log(logAppender);

            using var fixture = new EmptyRepositoryFixture();
            fixture.Repository.MakeACommit();

            var gitVersionOptions = new GitVersionOptions { WorkingDirectory = fixture.RepositoryPath };

            var gitVersionCalculator = GetGitVersionCalculator(gitVersionOptions, log);

            var versionVariables = gitVersionCalculator.CalculateVersionVariables();
            versionVariables.AssemblySemVer.ShouldBe("0.1.0.0");

            fileSystem.WriteAllText(versionVariables.FileName, versionCacheFileContent);
            versionVariables = gitVersionCalculator.CalculateVersionVariables();
            versionVariables.AssemblySemVer.ShouldBe("4.10.3.0");

            var logsMessages = stringBuilder.ToString();

            logsMessages.ShouldContain("Deserializing version variables from cache file", () => logsMessages);
        }

        [Test]
        public void CacheFileExistsOnDiskWhenOverrideConfigIsSpecifiedVersionShouldBeDynamicallyCalculatedWithoutSavingInCache()
        {
            const string versionCacheFileContent = @"
        Major: 4
        Minor: 10
        Patch: 3
        PreReleaseTag: test.19
        PreReleaseTagWithDash: -test.19
        PreReleaseLabel: test
        PreReleaseNumber: 19
        BuildMetaData:
        BuildMetaDataPadded:
        FullBuildMetaData: Branch.feature/test.Sha.dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        MajorMinorPatch: 4.10.3
        SemVer: 4.10.3-test.19
        LegacySemVer: 4.10.3-test19
        LegacySemVerPadded: 4.10.3-test0019
        AssemblySemVer: 4.10.3.0
        AssemblySemFileVer: 4.10.3.0
        FullSemVer: 4.10.3-test.19
        InformationalVersion: 4.10.3-test.19+Branch.feature/test.Sha.dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        BranchName: feature/test
        EscapedBranchName: feature-test
        Sha: dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        ShortSha: dd2a29af
        NuGetVersionV2: 4.10.3-test0019
        NuGetVersion: 4.10.3-test0019
        NuGetPreReleaseTagV2: test0019
        NuGetPreReleaseTag: test0019
        CommitsSinceVersionSource: 19
        CommitsSinceVersionSourcePadded: 0019
        CommitDate: 2015-11-10
        ";

            using var fixture = new EmptyRepositoryFixture();
            fixture.Repository.MakeACommit();

            var gitVersionOptions = new GitVersionOptions { WorkingDirectory = fixture.RepositoryPath };
            var gitVersionCalculator = GetGitVersionCalculator(gitVersionOptions, log);

            var versionVariables = gitVersionCalculator.CalculateVersionVariables();
            versionVariables.AssemblySemVer.ShouldBe("0.1.0.0");

            fileSystem.WriteAllText(versionVariables.FileName, versionCacheFileContent);

            var cacheDirectory = gitVersionCache.GetCacheDirectory();

            var cacheDirectoryTimestamp = fileSystem.GetLastDirectoryWrite(cacheDirectory);

            var config = new Config { TagPrefix = "prefix" };
            config.Reset();
            gitVersionOptions = new GitVersionOptions { WorkingDirectory = fixture.RepositoryPath, ConfigInfo = { OverrideConfig = config } };

            gitVersionCalculator = GetGitVersionCalculator(gitVersionOptions);
            versionVariables = gitVersionCalculator.CalculateVersionVariables();

            versionVariables.AssemblySemVer.ShouldBe("0.1.0.0");

            var cachedDirectoryTimestampAfter = fileSystem.GetLastDirectoryWrite(cacheDirectory);
            cachedDirectoryTimestampAfter.ShouldBe(cacheDirectoryTimestamp, () => "Cache was updated when override config was set");
        }

        [Test]
        public void CacheFileIsMissing()
        {
            var stringBuilder = new StringBuilder();
            void Action(string s) => stringBuilder.AppendLine(s);

            var logAppender = new TestLogAppender(Action);
            log = new Log(logAppender);

            using var fixture = new EmptyRepositoryFixture();

            var gitVersionOptions = new GitVersionOptions { WorkingDirectory = fixture.RepositoryPath };

            fixture.Repository.MakeACommit();
            var gitVersionCalculator = GetGitVersionCalculator(gitVersionOptions, log, fixture.Repository);

            gitVersionCalculator.CalculateVersionVariables();

            var logsMessages = stringBuilder.ToString();
            logsMessages.ShouldContain("yml not found", () => logsMessages);
        }

        [Test]
        public void ConfigChangeInvalidatesCache()
        {
            const string versionCacheFileContent = @"
        Major: 4
        Minor: 10
        Patch: 3
        PreReleaseTag: test.19
        PreReleaseTagWithDash: -test.19
        PreReleaseLabel: test
        PreReleaseNumber: 19
        WeightedPreReleaseNumber: 19
        BuildMetaData:
        BuildMetaDataPadded:
        FullBuildMetaData: Branch.feature/test.Sha.dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        MajorMinorPatch: 4.10.3
        SemVer: 4.10.3-test.19
        LegacySemVer: 4.10.3-test19
        LegacySemVerPadded: 4.10.3-test0019
        AssemblySemVer: 4.10.3.0
        AssemblySemFileVer: 4.10.3.0
        FullSemVer: 4.10.3-test.19
        InformationalVersion: 4.10.3-test.19+Branch.feature/test.Sha.dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        BranchName: feature/test
        EscapedBranchName: feature-test
        Sha: dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        ShortSha: dd2a29af
        NuGetVersionV2: 4.10.3-test0019
        NuGetVersion: 4.10.3-test0019
        NuGetPreReleaseTagV2: test0019
        NuGetPreReleaseTag: test0019
        VersionSourceSha: 4.10.2
        CommitsSinceVersionSource: 19
        CommitsSinceVersionSourcePadded: 0019
        CommitDate: 2015-11-10
        ";

            using var fixture = new EmptyRepositoryFixture();

            var gitVersionOptions = new GitVersionOptions { WorkingDirectory = fixture.RepositoryPath };

            fixture.Repository.MakeACommit();

            var gitVersionCalculator = GetGitVersionCalculator(gitVersionOptions);
            var versionVariables = gitVersionCalculator.CalculateVersionVariables();

            versionVariables.AssemblySemVer.ShouldBe("0.1.0.0");
            versionVariables.FileName.ShouldNotBeNullOrEmpty();

            fileSystem.WriteAllText(versionVariables.FileName, versionCacheFileContent);

            versionVariables = gitVersionCalculator.CalculateVersionVariables();
            versionVariables.AssemblySemVer.ShouldBe("4.10.3.0");

            var configPath = Path.Combine(fixture.RepositoryPath, DefaultConfigFileLocator.DefaultFileName);
            fileSystem.WriteAllText(configPath, "next-version: 5.0");

            gitVersionCalculator = GetGitVersionCalculator(gitVersionOptions, fs: fileSystem);

            versionVariables = gitVersionCalculator.CalculateVersionVariables();
            versionVariables.AssemblySemVer.ShouldBe("5.0.0.0");
        }

        [Test]
        public void NoCacheBypassesCache()
        {
            const string versionCacheFileContent = @"
        Major: 4
        Minor: 10
        Patch: 3
        PreReleaseTag: test.19
        PreReleaseTagWithDash: -test.19
        PreReleaseLabel: test
        PreReleaseNumber: 19
        WeightedPreReleaseNumber: 19
        BuildMetaData:
        BuildMetaDataPadded:
        FullBuildMetaData: Branch.feature/test.Sha.dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        MajorMinorPatch: 4.10.3
        SemVer: 4.10.3-test.19
        LegacySemVer: 4.10.3-test19
        LegacySemVerPadded: 4.10.3-test0019
        AssemblySemVer: 4.10.3.0
        AssemblySemFileVer: 4.10.3.0
        FullSemVer: 4.10.3-test.19
        InformationalVersion: 4.10.3-test.19+Branch.feature/test.Sha.dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        BranchName: feature/test
        EscapedBranchName: feature-test
        Sha: dd2a29aff0c948e1bdf3dabbe13e1576e70d5f9f
        ShortSha: dd2a29af
        NuGetVersionV2: 4.10.3-test0019
        NuGetVersion: 4.10.3-test0019
        NuGetPreReleaseTagV2: test0019
        NuGetPreReleaseTag: test0019
        VersionSourceSha: 4.10.2
        CommitsSinceVersionSource: 19
        CommitsSinceVersionSourcePadded: 0019
        CommitDate: 2015-11-10
        ";

            using var fixture = new EmptyRepositoryFixture();

            var gitVersionOptions = new GitVersionOptions { WorkingDirectory = fixture.RepositoryPath };

            fixture.Repository.MakeACommit();
            var gitVersionCalculator = GetGitVersionCalculator(gitVersionOptions);

            var versionVariables = gitVersionCalculator.CalculateVersionVariables();

            versionVariables.AssemblySemVer.ShouldBe("0.1.0.0");
            versionVariables.FileName.ShouldNotBeNullOrEmpty();

            fileSystem.WriteAllText(versionVariables.FileName, versionCacheFileContent);
            versionVariables = gitVersionCalculator.CalculateVersionVariables();
            versionVariables.AssemblySemVer.ShouldBe("4.10.3.0");

            gitVersionOptions.Settings.NoCache = true;
            versionVariables = gitVersionCalculator.CalculateVersionVariables();
            versionVariables.AssemblySemVer.ShouldBe("0.1.0.0");
        }

        [Test]
        public void WorkingDirectoryWithoutGit()
        {
            var gitVersionOptions = new GitVersionOptions { WorkingDirectory = Environment.SystemDirectory };

            var exception = Assert.Throws<DirectoryNotFoundException>(() =>
            {
                var gitVersionCalculator = GetGitVersionCalculator(gitVersionOptions);
                gitVersionCalculator.CalculateVersionVariables();
            });
            exception.Message.ShouldContain("Can't find the .git directory in");
        }

        [Test]
        [Category("NoMono")]
        [Description("LibGit2Sharp fails when running under Mono")]
        public void GetProjectRootDirectoryWorkingDirectoryWithWorktree()
        {
            using var fixture = new EmptyRepositoryFixture();
            fixture.Repository.MakeACommit();

            var worktreePath = Path.Combine(Directory.GetParent(fixture.RepositoryPath).FullName, Guid.NewGuid().ToString());
            try
            {
                // create a branch and a new worktree for it
                var repo = new Repository(fixture.RepositoryPath);
                repo.Worktrees.Add("worktree", worktreePath, false);

                var targetUrl = "https://github.com/GitTools/GitVersion.git";

                var gitVersionOptions = new GitVersionOptions
                {
                    RepositoryInfo = { TargetUrl = targetUrl },
                    WorkingDirectory = worktreePath
                };

                sp = GetServiceProvider(gitVersionOptions);

                gitVersionOptions.ProjectRootDirectory.TrimEnd('/', '\\').ShouldBe(worktreePath);
            }
            finally
            {
                DirectoryHelper.DeleteDirectory(worktreePath);
            }
        }

        [Test]
        public void GetProjectRootDirectoryNoWorktree()
        {
            using var fixture = new EmptyRepositoryFixture();
            var targetUrl = "https://github.com/GitTools/GitVersion.git";

            var gitVersionOptions = new GitVersionOptions
            {
                RepositoryInfo = { TargetUrl = targetUrl },
                WorkingDirectory = fixture.RepositoryPath
            };

            sp = GetServiceProvider(gitVersionOptions);

            var expectedPath = fixture.RepositoryPath.TrimEnd('/', '\\');
            gitVersionOptions.ProjectRootDirectory.TrimEnd('/', '\\').ShouldBe(expectedPath);
        }

        [Test]
        public void DynamicRepositoriesShouldNotErrorWithFailedToFindGitDirectory()
        {
            using var fixture = new EmptyRepositoryFixture();
            fixture.Repository.MakeACommit();

            var gitVersionOptions = new GitVersionOptions
            {
                WorkingDirectory = fixture.RepositoryPath,
                RepositoryInfo =
                {
                    TargetUrl = "https://github.com/GitTools/GitVersion.git",
                    TargetBranch = "refs/head/master"
                }
            };

            var gitVersionCalculator = GetGitVersionCalculator(gitVersionOptions, repository: fixture.Repository);
            gitPreparer.Prepare();
            gitVersionCalculator.CalculateVersionVariables();
        }

        [Test]
        [Ignore("temp")]
        public void GetDotGitDirectoryNoWorktree()
        {
            using var fixture = new EmptyRepositoryFixture();

            var gitVersionOptions = new GitVersionOptions
            {
                WorkingDirectory = fixture.RepositoryPath
            };

            sp = GetServiceProvider(gitVersionOptions);

            var expectedPath = Path.Combine(fixture.RepositoryPath, ".git");
            gitVersionOptions.DotGitDirectory.ShouldBe(expectedPath);
        }

        [Test]
        [Category("NoMono")]
        [Description("LibGit2Sharp fails when running under Mono")]
        [Ignore("temp")]
        public void GetDotGitDirectoryWorktree()
        {
            using var fixture = new EmptyRepositoryFixture();
            fixture.Repository.MakeACommit();

            var worktreePath = Path.Combine(Directory.GetParent(fixture.RepositoryPath).FullName, Guid.NewGuid().ToString());
            try
            {
                // create a branch and a new worktree for it
                var repo = new Repository(fixture.RepositoryPath);
                repo.Worktrees.Add("worktree", worktreePath, false);

                var gitVersionOptions = new GitVersionOptions
                {
                    WorkingDirectory = worktreePath
                };

                sp = GetServiceProvider(gitVersionOptions);

                var expectedPath = Path.Combine(fixture.RepositoryPath, ".git");
                gitVersionOptions.DotGitDirectory.ShouldBe(expectedPath);
            }
            finally
            {
                DirectoryHelper.DeleteDirectory(worktreePath);
            }
        }

        private IGitVersionTool GetGitVersionCalculator(GitVersionOptions gitVersionOptions, ILog logger = null, IRepository repository = null, IFileSystem fs = null)
        {
            sp = GetServiceProvider(gitVersionOptions, logger, repository, fs);

            fileSystem = sp.GetService<IFileSystem>();
            log = sp.GetService<ILog>();
            gitVersionCache = sp.GetService<IGitVersionCache>();
            gitPreparer = sp.GetService<IGitPreparer>();

            return sp.GetService<IGitVersionTool>();
        }

        private static IServiceProvider GetServiceProvider(GitVersionOptions gitVersionOptions, ILog log = null, IRepository repository = null, IFileSystem fileSystem = null, IEnvironment environment = null)
        {
            return ConfigureServices(services =>
            {
                if (log != null) services.AddSingleton(log);
                if (fileSystem != null) services.AddSingleton(fileSystem);
                if (repository != null) services.AddSingleton(repository);
                if (environment != null) services.AddSingleton(environment);
                services.AddSingleton(Options.Create(gitVersionOptions));
            });
        }
    }
}
