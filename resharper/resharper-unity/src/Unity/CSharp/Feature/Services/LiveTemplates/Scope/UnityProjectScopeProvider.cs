﻿using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.ProjectModel.Properties.Managed;
using JetBrains.ReSharper.Feature.Services.LiveTemplates.Context;
using JetBrains.ReSharper.Feature.Services.LiveTemplates.Scope;
using JetBrains.ReSharper.Plugins.Unity.Core.ProjectModel;
using JetBrains.Util;

namespace JetBrains.ReSharper.Plugins.Unity.CSharp.Feature.Services.LiveTemplates.Scope
{
    [ShellComponent]
    public class UnityProjectScopeProvider : ScopeProvider
    {
        public UnityProjectScopeProvider()
        {
            // These factory methods are used to create scope points when reading templates from settings
            Creators.Add(TryToCreate<UnityFileTemplateSectionMarker>);
            Creators.Add(TryToCreate<InUnityCSharpProject>);
            Creators.Add(TryToCreate<InUnityCSharpAssetsFolder>);
            Creators.Add(TryToCreate<InUnityCSharpEditorFolder>);
            Creators.Add(TryToCreate<InUnityCSharpRuntimeFolder>);
            Creators.Add(TryToCreate<InUnityCSharpFirstpassFolder>);
            Creators.Add(TryToCreate<InUnityCSharpFirstpassEditorFolder>);
            Creators.Add(TryToCreate<InUnityCSharpFirstpassRuntimeFolder>);
        }

        public override IEnumerable<ITemplateScopePoint> ProvideScopePoints(TemplateAcceptanceContext context)
        {
            if (!context.Solution.HasUnityReference())
                yield break;

            var project = context.GetProject();
            if (project != null && !project.IsUnityProject())
                yield break;

            // TODO: This returns the C# Unity project scope even if there's no project!
            // E.g. current context is a folder under Assets or Packages that isn't part of a PSI project
            yield return new InUnityCSharpProject();

            var folders = GetFoldersFromProjectFolder(context) ?? GetFoldersFromPath(context);
            if (folders == null || folders.IsEmpty())
                yield break;

            // TODO: Review all scope points
            // See JetBrains/resharper-unity#1922

            var rootFolder = folders[folders.Count - 1];
            if (rootFolder.Equals(ProjectExtensions.AssetsFolder, StringComparison.OrdinalIgnoreCase))
            {
                yield return new InUnityCSharpAssetsFolder();

                var isFirstpass = IsFirstpass(folders);
                var isEditor = folders.Any(f => f.Equals("Editor", StringComparison.OrdinalIgnoreCase));

                if (isFirstpass)
                {
                    yield return new InUnityCSharpFirstpassFolder();
                    if (isEditor)
                        yield return new InUnityCSharpFirstpassEditorFolder();
                    else
                        yield return new InUnityCSharpFirstpassRuntimeFolder();
                }
                else
                {
                    if (isEditor)
                        yield return new InUnityCSharpEditorFolder();
                    else
                        yield return new InUnityCSharpRuntimeFolder();
                }
            }

            if (!project.IsOneOfPredefinedUnityProjects())
            {
                // For a project with UNITY_EDITOR define we have to allow Editor Templates 
                if (project != null && project.ProjectProperties.ActiveConfigurations.Configurations
                    .OfType<IManagedProjectConfiguration>()
                    .Select(configuration => configuration.DefineConstants)
                    .All(defines => defines.Contains("UNITY_EDITOR")))
                {
                    yield return new InUnityCSharpEditorFolder();
                }
            }
        }

        [CanBeNull]
        private List<string> GetFoldersFromProjectFolder(TemplateAcceptanceContext context)
        {
            var projectFolder = context.GetProjectFolder();
            if (projectFolder == null)
                return null;

            var folders = new List<string>();
            while (projectFolder?.Path?.ShortName != null)
            {
                folders.Add(projectFolder.Path.ShortName);
                projectFolder = projectFolder.ParentFolder;
            }
            return folders;
        }

        [CanBeNull]
        private List<string> GetFoldersFromPath(TemplateAcceptanceContext context)
        {
            if (context.Location == null)
                return null;

            var folders = new List<string>();
            var currentPath = context.Location;
            while (!currentPath.IsEmpty)
            {
                var folder = currentPath.Name;
                folders.Add(folder);
                if (folder == ProjectExtensions.AssetsFolder)
                    break;
                currentPath = currentPath.Parent;
            }
            return folders;
        }

        private bool IsFirstpass(List<string> folders)
        {
            if (folders.Count > 1)
            {
                // We already know that folders[folders.Count - 1] == "Assets"
                var toplevelFolder = folders[folders.Count - 2];
                return toplevelFolder.Equals("Standard Assets", StringComparison.OrdinalIgnoreCase)
                       || toplevelFolder.Equals("Pro Standard Assets", StringComparison.OrdinalIgnoreCase)
                       || toplevelFolder.Equals("Plugins", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }
}