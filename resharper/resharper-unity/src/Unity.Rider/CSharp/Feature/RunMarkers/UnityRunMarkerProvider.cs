using JetBrains.Application.Settings;
using JetBrains.Collections.Viewable;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Plugins.Unity.Core.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches.SymbolCache;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Rider.Backend.Features.RunMarkers;

namespace JetBrains.ReSharper.Plugins.Unity.Rider.CSharp.Feature.RunMarkers
{
    [Language(typeof(CSharpLanguage))]
    public class UnityRunMarkerProvider : IRunMarkerProvider
    {
        public void CollectRunMarkers(IFile file, IContextBoundSettingsStore settings, IHighlightingConsumer consumer)
        {
            if (!file.GetSolution().GetComponent<UnitySolutionTracker>().IsUnityProject.HasTrueValue()) return;
            if (!(file is ICSharpFile csharpFile)) return;

            foreach (var declaration in CachedDeclarationsCollector.Run<IMethodDeclaration>(csharpFile))
            {
                if (!(declaration.DeclaredElement is IMethod method)) continue;

                if (UnityRunMarkerUtil.IsSuitableStaticMethod(method))
                {
                    var range = declaration.GetNameDocumentRange();
                    var highlighting = new UnityRunMarkerHighlighting(
                        declaration, UnityRunMarkerAttributeIds.RUN_METHOD_MARKER_ID, range,
                        file.GetPsiModule().TargetFrameworkId);
                    consumer.AddHighlighting(highlighting, range);
                }
            }
        }

        public double Priority => RunMarkerProviderPriority.DEFAULT;
    }
}