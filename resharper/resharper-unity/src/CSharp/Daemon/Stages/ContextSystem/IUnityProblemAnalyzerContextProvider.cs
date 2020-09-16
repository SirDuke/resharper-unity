using JetBrains.Annotations;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;

namespace JetBrains.ReSharper.Plugins.Unity.CSharp.Daemon.Stages.ContextSystem
{
    /// <summary>
    /// Checks if tree node has context.
    /// Each context must have exactly 1 context provider.
    /// </summary>
    public interface IUnityProblemAnalyzerContextProvider : IUnityProblemAnalyzerContextClassification
    {
        UnityProblemAnalyzerContextElement GetContext([CanBeNull] ITreeNode node, DaemonProcessKind processKind);
        bool IsMarked([CanBeNull] IDeclaredElement node, DaemonProcessKind processKind);
        bool IsEnabled { get; }
    }

    public static class UnityProblemAnalyzerContextProviderUtil
    {
        public static bool HasContext([NotNull] this IUnityProblemAnalyzerContextProvider provider, [CanBeNull] ITreeNode node, DaemonProcessKind processKind)
        {
            return provider.GetContext(node, processKind) == provider.Context;
        }
    }
}