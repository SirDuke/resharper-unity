using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using JetBrains.Application.changes;
using JetBrains.Application.FileSystemTracker;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.Collections;
using JetBrains.Collections.Viewable;
using JetBrains.DataFlow;
using JetBrains.Diagnostics;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Impl;
using JetBrains.ReSharper.Plugins.Unity.Packages;
using JetBrains.ReSharper.Plugins.Unity.ProjectModel;
using JetBrains.ReSharper.Plugins.Unity.Utils;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Psi.Modules.ExternalFileModules;
using JetBrains.Util;
using JetBrains.Util.dataStructures;

namespace JetBrains.ReSharper.Plugins.Unity.Core.Psi.Modules
{
    [SolutionComponent]
    public class UnityExternalFilesModuleProcessor : IChangeProvider, IUnityReferenceChangeHandler
    {
        private const ulong AssetFileCheckSizeThreshold = 20 * (1024 * 1024); // 20 MB

        // stats
        public readonly List<ulong> PrefabSizes = new();
        public readonly List<ulong> SceneSizes = new();
        public readonly List<ulong> AssetSizes = new();
        public readonly List<ulong> AsmDefSizes = new();
        public readonly List<ulong> KnownBinaryAssetSizes = new();
        public readonly List<ulong> ExcludedByNameAssetsSizes = new();
        public int MetaFileCount;

        private readonly Lifetime myLifetime;
        private readonly ILogger myLogger;
        private readonly ISolution mySolution;
        private readonly ChangeManager myChangeManager;
        private readonly PackageManager myPackageManager;
        private readonly IShellLocks myLocks;
        private readonly IFileSystemTracker myFileSystemTracker;
        private readonly UnityExternalPsiSourceFileFactory myPsiSourceFileFactory;
        private readonly UnityExternalFilesModuleFactory myModuleFactory;
        private readonly UnityExternalFilesIndexDisablingStrategy myIndexDisablingStrategy;
        private readonly Dictionary<VirtualFileSystemPath, LifetimeDefinition> myRootPathLifetimes;
        private readonly VirtualFileSystemPath mySolutionDirectory;

        public UnityExternalFilesModuleProcessor(Lifetime lifetime, ILogger logger, ISolution solution,
                                                 ChangeManager changeManager,
                                                 IPsiModules psiModules,
                                                 PackageManager packageManager,
                                                 IShellLocks locks,
                                                 IFileSystemTracker fileSystemTracker,
                                                 UnityExternalPsiSourceFileFactory psiSourceFileFactory,
                                                 UnityExternalFilesModuleFactory moduleFactory,
                                                 UnityExternalFilesIndexDisablingStrategy indexDisablingStrategy)
        {
            myLifetime = lifetime;
            myLogger = logger;
            mySolution = solution;
            myChangeManager = changeManager;
            myPackageManager = packageManager;
            myLocks = locks;
            myFileSystemTracker = fileSystemTracker;
            myPsiSourceFileFactory = psiSourceFileFactory;
            myModuleFactory = moduleFactory;
            myIndexDisablingStrategy = indexDisablingStrategy;

            myRootPathLifetimes = new Dictionary<VirtualFileSystemPath, LifetimeDefinition>();

            // SolutionDirectory isn't absolute in tests, and will throw an exception if we use it when we call Exists
            mySolutionDirectory = solution.SolutionDirectory;
            if (!mySolutionDirectory.IsAbsolute)
                mySolutionDirectory = solution.SolutionDirectory.ToAbsolutePath(FileSystemUtil.GetCurrentDirectory().ToVirtualFileSystemPath());

            changeManager.RegisterChangeProvider(lifetime, this);
            changeManager.AddDependency(lifetime, psiModules, this);
        }

        // Called once when we know it's a Unity solution. I.e. a solution that has a Unity reference (so can be true
        // for non-generated solutions)
        public virtual void OnHasUnityReference()
        {
            // For project model access
            myLocks.AssertReadAccessAllowed();

            var externalFiles = new ExternalFiles();
            CollectExternalFilesForSolutionDirectory(externalFiles, "Assets");
            CollectExternalFilesForSolutionDirectory(externalFiles, "ProjectSettings");
            CollectExternalFilesForPackages(externalFiles);

            // Disable asset indexing for massive projects. Note that we still collect all files, and always index
            // project settings and meta files.
            myIndexDisablingStrategy.Run(externalFiles.AssetFiles);

            AddExternalFiles(externalFiles);

            // TODO: Capture read-only package stats separately
            UpdateStatistics(externalFiles);

            SubscribeToPackageUpdates();
        }

        public void OnUnityProjectAdded(Lifetime projectLifetime, IProject project)
        {
            // Do nothing. A project will either be in Assets or in a package, so either way, we've got it covered.
        }

        // This method is safe to call multiple times with the same folder (or sub folder)
        private void CollectExternalFilesForSolutionDirectory(ExternalFiles externalFiles, string relativePath)
        {
            var path = mySolutionDirectory.Combine(relativePath);
            if (path.ExistsDirectory)
                CollectExternalFilesForDirectory(externalFiles, path);
        }

        private void CollectExternalFilesForDirectory(ExternalFiles externalFiles, VirtualFileSystemPath directory)
        {
            // Don't process the entire solution directory - this would process Assets and Packages for a second time,
            // and also process Temp and Library, which are likely to be huge. This is unlikely, but be safe.
            if (directory == mySolutionDirectory)
            {
                myLogger.Error("Unexpected request to process entire solution directory. Skipping");
                return;
            }

            if (myRootPathLifetimes.ContainsKey(directory))
                return;

            // Make sure the directory hasn't already been processed as part of a previous directory. This shouldn't
            // happen, as we index based on folder or package, not project, so there is no way for us to see nested
            // folders
            foreach (var rootPath in myRootPathLifetimes.Keys)
            {
                if (rootPath.IsPrefixOf(directory))
                    return;
            }

            myLogger.Info("Processing directory for asset and meta files: {0}", directory);

            // Based on super simple tests, GetDirectoryEntries is faster than GetChildFiles with subsequent calls to
            // GetFileLength. But what is more surprising is that Windows in a VM is an order of magnitude FASTER than
            // Mac, on the same project!
            var entries = directory.GetDirectoryEntries("*", PathSearchFlags.RecurseIntoSubdirectories
                                                             | PathSearchFlags.ExcludeDirectories);

            foreach (var entry in entries)
            {
                // Ignore anything under a folder that ends with a tilde - Unity does not import these folders into the
                // asset database, so they're not part of this project
                if (entry.IsFile && !entry.RelativePath.FullPath.Contains("~/") &&
                    !entry.RelativePath.FullPath.Contains("~\\"))
                {
                    externalFiles.ProcessExternalFile(entry, mySolution);
                }
            }

            externalFiles.AddDirectory(directory);
            myRootPathLifetimes.Add(directory, myLifetime.CreateNested());
        }

        private void CollectExternalFilesForPackages(ExternalFiles externalFiles)
        {
            foreach (var (_ , packageData) in myPackageManager.Packages)
            {
                if (packageData.PackageFolder == null || packageData.PackageFolder.IsEmpty)
                    continue;

                // Index the whole of the package folder. All assets under a package are included into the Unity project
                // although only folders with a `.asmdef` will be treated as source and compiled into an assembly
                CollectExternalFilesForDirectory(externalFiles, packageData.PackageFolder);
            }
        }

        private void SubscribeToPackageUpdates()
        {
            // We've already processed all packages that were available when the project was first loaded, so this will
            // just be updating a single package at a time - Unity doesn't offer "update all".
            myPackageManager.Packages.AddRemove.Advise_NoAcknowledgement(myLifetime, args =>
            {
                var packageData = args.Value.Value;
                if (packageData.PackageFolder == null || packageData.PackageFolder.IsEmpty ||
                    myModuleFactory.PsiModule == null)
                {
                    return;
                }

                if (args.Action == AddRemove.Add)
                {
                    var externalFiles = new ExternalFiles();
                    CollectExternalFilesForDirectory(externalFiles, packageData.PackageFolder);
                    AddExternalFiles(externalFiles);
                }
                else
                {
                    var psiModuleChanges = new PsiModuleChangeBuilder();
                    foreach (var sourceFile in myModuleFactory.PsiModule.GetSourceFilesByRootFolder(packageData.PackageFolder))
                        psiModuleChanges.AddFileChange(sourceFile, PsiModuleChange.ChangeType.Removed);
                    FlushChanges(psiModuleChanges);

                    if (!myRootPathLifetimes.TryGetValue(packageData.PackageFolder, out var lifetimeDefinition))
                        myLogger.Warn("Cannot find lifetime for watched folder: {0}", packageData.PackageFolder);

                    lifetimeDefinition?.Terminate();
                    myRootPathLifetimes.Remove(packageData.PackageFolder);
                }
            });
        }

        private void AddExternalFiles([NotNull] ExternalFiles externalFiles)
        {
            var builder = new PsiModuleChangeBuilder();
            AddExternalPsiSourceFiles(externalFiles.MetaFiles, builder);
            AddExternalPsiSourceFiles(externalFiles.AssetFiles, builder);
            AddExternalPsiSourceFiles(externalFiles.AsmDefFiles, builder);
            FlushChanges(builder);

            // We should only start watching for file system changes after adding the files we know about
            foreach (var directory in externalFiles.Directories)
            {
                var lifetime = myRootPathLifetimes[directory].Lifetime;
                myFileSystemTracker.AdviseDirectoryChanges(lifetime, directory, true, OnWatchedDirectoryChange);
            }
        }

        private void AddExternalPsiSourceFiles(List<VirtualDirectoryEntryData> files, PsiModuleChangeBuilder builder)
        {
            foreach (var directoryEntry in files)
                 AddOrUpdateExternalPsiSourceFile(builder, directoryEntry.GetAbsolutePath());
        }

        private void AddOrUpdateExternalPsiSourceFile(PsiModuleChangeBuilder builder, VirtualFileSystemPath path)
        {
            Assertion.AssertNotNull(myModuleFactory.PsiModule, "myModuleFactory.PsiModule != null");

            var sourceFile = GetExternalPsiSourceFile(myModuleFactory.PsiModule, path);
            if (sourceFile != null)
            {
                // We already know this file. Make sure it's up to date
                UpdateExternalPsiSourceFile(sourceFile, builder, path);
                return;
            }

            sourceFile = myPsiSourceFileFactory.CreateExternalPsiSourceFile(myModuleFactory.PsiModule, path);
            builder.AddFileChange(sourceFile, PsiModuleChange.ChangeType.Added);
        }

        private static void UpdateExternalPsiSourceFile([CanBeNull] IPsiSourceFile sourceFile,
                                                        PsiModuleChangeBuilder builder,
                                                        VirtualFileSystemPath path)
        {
            if (sourceFile == null) return;

            // Make sure we update the cached file system data, or all of the ICache implementations will think the
            // file is already up to date
            (sourceFile as PsiSourceFileFromPath)?.GetCachedFileSystemData().Refresh(path);
            builder.AddFileChange(sourceFile, PsiModuleChange.ChangeType.Modified);
        }

        private void UpdateStatistics(ExternalFiles externalFiles)
        {
            foreach (var directoryEntry in externalFiles.AssetFiles)
            {
                if (directoryEntry.RelativePath.IsAsset())
                    AssetSizes.Add(directoryEntry.Length);
                else if (directoryEntry.RelativePath.IsPrefab())
                    PrefabSizes.Add(directoryEntry.Length);
                else if (directoryEntry.RelativePath.IsScene())
                    SceneSizes.Add(directoryEntry.Length);
                else if (directoryEntry.RelativePath.IsAsmDef())
                    AsmDefSizes.Add(directoryEntry.Length);
            }

            MetaFileCount += externalFiles.MetaFiles.Count;

            foreach (var directoryEntry in externalFiles.KnownBinaryAssetFiles)
                KnownBinaryAssetSizes.Add(directoryEntry.Length);

            foreach (var directoryEntry in externalFiles.ExcludedByNameAssetFiles)
                ExcludedByNameAssetsSizes.Add(directoryEntry.Length);
        }

        private static bool IsIndexedExternalFile(VirtualFileSystemPath path)
        {
            return path.IsIndexedExternalFile() && !IsBinaryAsset(path) && !IsAssetExcludedByName(path);
        }

        private static bool IsBinaryAsset(VirtualDirectoryEntryData directoryEntry)
        {
            if (IsKnownBinaryAssetByName(directoryEntry.RelativePath))
                return true;

            return directoryEntry.Length > AssetFileCheckSizeThreshold && directoryEntry.RelativePath.IsAsset() &&
                   !directoryEntry.GetAbsolutePath().SniffYamlHeader();
        }

        private static bool IsBinaryAsset(VirtualFileSystemPath path)
        {
            if (IsKnownBinaryAssetByName(path))
                return true;

            if (!path.ExistsFile)
                return false;

            var fileLength = (ulong) path.GetFileLength();
            return fileLength > AssetFileCheckSizeThreshold && path.IsAsset() && !path.SniffYamlHeader();
        }

        private static bool IsKnownBinaryAssetByName(IPath path)
        {
            // Even if the project is set to ForceText, some files will always be binary, notably LightingData.asset.
            // Users can also force assets to serialise as binary with the [PreferBinarySerialization] attribute
            return path.Name.Equals("LightingData.asset", StringComparison.InvariantCultureIgnoreCase);
        }

        private static bool IsAssetExcludedByName(IPath path)
        {
            // NavMesh.asset can sometimes be binary, sometimes text. I don't know the criteria for when one format is
            // picked over another. OcclusionCullingData.asset is usually text, but large and contains long streams of
            // ascii-based "binary". Neither file contains anything we're interested in, and simply increases parsing
            // and indexing time
            var filename = path.Name;
            return filename.Equals("NavMesh.asset", StringComparison.InvariantCultureIgnoreCase)
                || filename.Equals("OcclusionCullingData.asset", StringComparison.InvariantCultureIgnoreCase);
        }

        private void OnWatchedDirectoryChange(FileSystemChangeDelta delta)
        {
            myLocks.ExecuteOrQueue(Lifetime.Eternal, "UnityExternalFilesModuleProcessor::OnWatchedDirectoryChange",
                () =>
                {
                    var builder = new PsiModuleChangeBuilder();
                    ProcessFileSystemChangeDelta(delta, builder);
                    FlushChanges(builder);
                });
        }

        private void ProcessFileSystemChangeDelta(FileSystemChangeDelta delta, PsiModuleChangeBuilder builder)
        {
            var module = myModuleFactory.PsiModule;
            if (module == null)
                return;

            IPsiSourceFile sourceFile;
            switch (delta.ChangeType)
            {
                // We can get ADDED for a file we already know about if an app saves the file by saving to a temp file
                // first. We don't get a DELETED first, surprisingly. Treat this scenario like CHANGED
                case FileSystemChangeType.ADDED:
                    if (IsIndexedExternalFile(delta.NewPath))
                        AddOrUpdateExternalPsiSourceFile(builder, delta.NewPath);
                    break;

                case FileSystemChangeType.DELETED:
                    sourceFile = GetExternalPsiSourceFile(module, delta.OldPath);
                    if (sourceFile != null)
                        builder.AddFileChange(sourceFile, PsiModuleChange.ChangeType.Removed);
                    break;

                // We can get RENAMED if an app saves the file by saving to a temporary name first, then renaming
                case FileSystemChangeType.CHANGED:
                case FileSystemChangeType.RENAMED:
                    sourceFile = GetExternalPsiSourceFile(module, delta.NewPath);
                    UpdateExternalPsiSourceFile(sourceFile, builder, delta.NewPath);
                    break;

                case FileSystemChangeType.SUBTREE_CHANGED:
                case FileSystemChangeType.UNKNOWN:
                    break;
            }

            foreach (var child in delta.GetChildren())
                ProcessFileSystemChangeDelta(child, builder);
        }

        [CanBeNull]
        private static IPsiSourceFile GetExternalPsiSourceFile([NotNull] IPsiModuleOnFileSystemPaths module,
                                                               VirtualFileSystemPath path)
        {
            return module.TryGetFileByPath(path, out var sourceFile) ? sourceFile : null;
        }

        private void FlushChanges(PsiModuleChangeBuilder builder)
        {
            if (builder.IsEmpty)
                return;

            myLocks.ExecuteOrQueueEx(myLifetime, GetType().Name + ".FlushChanges",
                () =>
                {
                    var module = myModuleFactory.PsiModule;
                    Assertion.AssertNotNull(module, "module != null");
                    myLocks.AssertMainThread();
                    using (myLocks.UsingWriteLock())
                    {
                        var psiModuleChange = builder.Result;
                        foreach (var fileChange in psiModuleChange.FileChanges)
                        {
                            var location = fileChange.Item.GetLocation();
                            if (location.IsEmpty)
                                continue;

                            switch (fileChange.Type)
                            {
                                case PsiModuleChange.ChangeType.Added:
                                    module.Add(location, fileChange.Item, null);
                                    break;

                                case PsiModuleChange.ChangeType.Removed:
                                    module.Remove(location);
                                    break;
                            }
                        }

                        myChangeManager.OnProviderChanged(this, psiModuleChange, SimpleTaskExecutor.Instance);
                    }
                });
        }

        public object Execute(IChangeMap changeMap) => null;

        private class ExternalFiles
        {
            public readonly List<VirtualDirectoryEntryData> MetaFiles = new();
            public readonly List<VirtualDirectoryEntryData> AssetFiles = new();
            public readonly List<VirtualDirectoryEntryData> AsmDefFiles = new();
            public FrugalLocalList<VirtualDirectoryEntryData> KnownBinaryAssetFiles;
            public FrugalLocalList<VirtualDirectoryEntryData> ExcludedByNameAssetFiles;
            public FrugalLocalList<VirtualFileSystemPath> Directories;

            public void ProcessExternalFile(VirtualDirectoryEntryData directoryEntry, ISolution solution)
            {
                solution.Locks.AssertReadAccessAllowed();

                if (directoryEntry.RelativePath.IsMeta())
                    MetaFiles.Add(directoryEntry);
                else if (directoryEntry.RelativePath.IsIndexedYamlExternalFile())
                {
                    if (IsBinaryAsset(directoryEntry))
                        KnownBinaryAssetFiles.Add(directoryEntry);
                    else if (IsAssetExcludedByName(directoryEntry.RelativePath))
                        ExcludedByNameAssetFiles.Add(directoryEntry);
                    else
                        AssetFiles.Add(directoryEntry);
                }
                else if (directoryEntry.RelativePath.IsAsmDef())
                {
                    // We're collecting external files, so skip it if it's part of a project. This might be because it's
                    // an editable package, or because the user has package project generation enabled.
                    // This requires read lock!
                    if (!solution.FindProjectItemsByLocation(directoryEntry.GetAbsolutePath()).Any())
                        AsmDefFiles.Add(directoryEntry);
                }
            }

            public void AddDirectory(VirtualFileSystemPath directory)
            {
                Directories.Add(directory);
            }
        }
    }
}