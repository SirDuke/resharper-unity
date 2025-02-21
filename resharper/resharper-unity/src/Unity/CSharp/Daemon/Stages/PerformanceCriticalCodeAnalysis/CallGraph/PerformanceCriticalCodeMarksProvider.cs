using JetBrains.Annotations;
using JetBrains.Collections.Viewable;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.CSharp.CallGraph;
using JetBrains.ReSharper.Plugins.Unity.Core.ProjectModel;
using JetBrains.ReSharper.Plugins.Unity.CSharp.Psi.Resolve;
using JetBrains.ReSharper.Plugins.Unity.UnityEditorIntegration.Api;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;

namespace JetBrains.ReSharper.Plugins.Unity.CSharp.Daemon.Stages.PerformanceCriticalCodeAnalysis.CallGraph
{
    [SolutionComponent]
    public class PerformanceCriticalCodeMarksProvider : PerformanceAnalysisRootMarksProviderBase
    {
        public const string MarkId = "Unity.PerformanceCriticalContext";

        public PerformanceCriticalCodeMarksProvider(Lifetime lifetime, ISolution solution,
            UnityReferencesTracker referencesTracker, UnitySolutionTracker tracker)
            : base(MarkId, new CallGraphOutcomingPropagator(solution, MarkId))
        {
            Enabled.Value = tracker.IsUnityProject.HasTrueValue();
            referencesTracker.HasUnityReference.Advise(lifetime, b => Enabled.Value = Enabled.Value | b);
        }

        private IDeclaredElement ExtractCoroutineOrInvokeRepeating(ITreeNode currentNode)
        {
            if (currentNode is IInvocationExpression invocationExpression)
            {
                // we should find 'StartCoroutine' method, because passed symbol will be hot too
                var reference = (invocationExpression.InvokedExpression as IReferenceExpression)?.Reference;
                if (reference == null)
                    return null;

                var info = reference.Resolve();
                if (info.ResolveErrorType != ResolveErrorType.OK)
                    return null;

                var declaredElement = info.DeclaredElement as IMethod;
                if (declaredElement == null)
                    return null;

                var containingType = declaredElement.GetContainingType();

                if (containingType == null || containingType.GetClrName().Equals(KnownTypes.MonoBehaviour))
                {
                    if (!declaredElement.ShortName.Equals("StartCoroutine") &&
                        !declaredElement.ShortName.Equals("InvokeRepeating"))
                    {
                        return null;
                    }

                    var firstArgument = invocationExpression.Arguments.FirstOrDefault()?.Value;
                    if (firstArgument == null)
                        return null;

                    return ExtractMethodDeclarationFromStartCoroutine(firstArgument);
                }
            }

            return null;
        }

        private IDeclaredElement ExtractMethodDeclarationFromStartCoroutine([NotNull] ICSharpExpression firstArgument)
        {
            // 'StartCoroutine' has overload with string. We have already attached reference, so get declaration from
            // reference
            if (firstArgument is ILiteralExpression literalExpression)
            {
                var coroutineMethodReference =
                    literalExpression.GetReferences<UnityEventFunctionReference>().FirstOrDefault();
                if (coroutineMethodReference != null)
                {
                    return coroutineMethodReference.Resolve().DeclaredElement;
                }
            }

            // argument is IEnumerator which is returned from invocation, so get invocation declaration
            if (firstArgument is IInvocationExpression coroutineInvocation)
            {
                var invocationReference = (coroutineInvocation.InvokedExpression as IReferenceExpression)?.Reference;
                var info = invocationReference?.Resolve();
                return info?.DeclaredElement;
            }

            return null;
        }

        public override LocalList<IDeclaredElement> GetRootMarksFromNode(ITreeNode currentNode,
            IDeclaredElement containingFunction)
        {
            var result = base.GetRootMarksFromNode(currentNode, containingFunction);
            var coroutineOrInvoke = ExtractCoroutineOrInvokeRepeating(currentNode);

            if (coroutineOrInvoke != null)
                result.Add(coroutineOrInvoke);

            // it means we are in functional type member like methodDeclaration
            if (containingFunction == null)
                return result;

            var declaration = currentNode as ITypeMemberDeclaration;
            var typeMember = declaration?.DeclaredElement;

            if (PerformanceCriticalCodeStageUtil.IsPerformanceCriticalRootMethod(typeMember))
                result.Add(typeMember);

            return result;
        }
    }
}