using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Progress;
using JetBrains.Application.UI.Actions.ActionManager;
using JetBrains.Application.UI.ActionsRevised.Handlers;
using JetBrains.Application.UI.ActionSystem;
using JetBrains.Diagnostics;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.UsageChecking;
using JetBrains.ReSharper.Feature.Services.Bulbs;
using JetBrains.ReSharper.Feature.Services.Intentions;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.InplaceRefactorings;
using JetBrains.ReSharper.Intentions.Util;
using JetBrains.ReSharper.Plugins.Unity.CSharp.Daemon.Errors;
using JetBrains.ReSharper.Plugins.Unity.UnityEditorIntegration.Api;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Refactorings.ChangeSignature;
using JetBrains.TextControl;
using JetBrains.Util;
using JetBrains.Util.dataStructures;

namespace JetBrains.ReSharper.Plugins.Unity.CSharp.Feature.Services.QuickFixes
{
    [QuickFix]
    public class IncorrectMethodSignatureQuickFix : IQuickFix
    {
        private readonly IMethodDeclaration myMethodDeclaration;
        private readonly FrugalLocalList<MethodSignature> myExpectedMethodSignatures;
        private readonly FrugalLocalList<MethodSignatureMatch> myMatches;

        public IncorrectMethodSignatureQuickFix(InvalidStaticModifierWarning warning)
            : this(warning.MethodDeclaration, warning.ExpectedMethodSignature,
                   MethodSignatureMatch.IncorrectStaticModifier)
        {
        }

        public IncorrectMethodSignatureQuickFix(InvalidParametersWarning warning)
            : this(warning.MethodDeclaration, warning.ExpectedMethodSignature,
                   MethodSignatureMatch.IncorrectParameters)
        {
        }

        public IncorrectMethodSignatureQuickFix(InvalidReturnTypeWarning warning)
            : this(warning.MethodDeclaration, warning.ExpectedMethodSignature, MethodSignatureMatch.IncorrectReturnType)
        {
        }

        public IncorrectMethodSignatureQuickFix(InvalidTypeParametersWarning warning)
            : this(warning.MethodDeclaration, warning.ExpectedMethodSignature,
                   MethodSignatureMatch.IncorrectTypeParameters)
        {
        }

        public IncorrectMethodSignatureQuickFix(IncorrectSignatureWarning warning)
            : this(warning.MethodDeclaration, warning.ExpectedMethodSignature, warning.MethodSignatureMatch)
        {
        }

        public IncorrectMethodSignatureQuickFix(IncorrectSignatureWithChoiceWarning warning)
            : this(warning.MethodDeclaration, warning.ExpectedMethodSignatures)
        {
        }

        private IncorrectMethodSignatureQuickFix(IMethodDeclaration methodDeclaration,
            MethodSignature expectedMethodSignature,
            MethodSignatureMatch match)
        {
            myMethodDeclaration = methodDeclaration;
            myExpectedMethodSignatures = new FrugalLocalList<MethodSignature>();
            myExpectedMethodSignatures.Add(expectedMethodSignature);
            myMatches = new FrugalLocalList<MethodSignatureMatch>();
            myMatches.Add(match);
        }

        private IncorrectMethodSignatureQuickFix(IMethodDeclaration methodDeclaration,
            MethodSignature[] expectedMethodSignatures)
        {
            myMethodDeclaration = methodDeclaration;
            myExpectedMethodSignatures = new FrugalLocalList<MethodSignature>(expectedMethodSignatures);
            myMatches = new FrugalLocalList<MethodSignatureMatch>();
            for (var i = 0; i < myExpectedMethodSignatures.Count; i++)
                myMatches.Add(MethodSignatureMatch.NoMatch);
        }

        public bool IsAvailable(IUserDataHolder cache) => ValidUtils.Valid(myMethodDeclaration);

        public IEnumerable<IntentionAction> CreateBulbItems()
        {
            var bulbs = new IntentionAction[myExpectedMethodSignatures.Count];
            for (int i = 0; i < myExpectedMethodSignatures.Count; i++)
            {
                bulbs[i] = new ChangeSignatureBulbAction(myMethodDeclaration, myExpectedMethodSignatures[i], myMatches[i]).ToQuickFixIntention();
            }
            return bulbs;
        }

        private class ChangeSignatureBulbAction : BulbActionBase
        {
            private readonly IMethodDeclaration myMethodDeclaration;
            private readonly MethodSignature myExpectedMethodSignature;
            private readonly MethodSignatureMatch myMatch;

            public ChangeSignatureBulbAction(IMethodDeclaration methodDeclaration, MethodSignature expectedMethodSignature, MethodSignatureMatch match)
            {
                myMethodDeclaration = methodDeclaration;
                myExpectedMethodSignature = expectedMethodSignature;
                myMatch = match;

                Text = GetText(methodDeclaration, expectedMethodSignature);
            }

            public override string Text { get; }

            protected override Action<ITextControl> ExecutePsiTransaction(ISolution solution,
                IProgressIndicator progress)
            {
                Action<ITextControl> action = null;

                if ((myMatch & MethodSignatureMatch.IncorrectStaticModifier) ==
                    MethodSignatureMatch.IncorrectStaticModifier && myExpectedMethodSignature.IsStatic.HasValue)
                {
                    myMethodDeclaration.SetStatic(myExpectedMethodSignature.IsStatic.Value);
                }

                if ((myMatch & MethodSignatureMatch.IncorrectParameters) == MethodSignatureMatch.IncorrectParameters)
                    action = ChangeParameters(solution);

                if ((myMatch & MethodSignatureMatch.IncorrectReturnType) == MethodSignatureMatch.IncorrectReturnType)
                {
                    var element = myMethodDeclaration.DeclaredElement;
                    Assertion.AssertNotNull(element, "element != null");

                    var language = myMethodDeclaration.Language;
                    var changeTypeHelper = LanguageManager.Instance.GetService<IChangeTypeHelper>(language);
                    changeTypeHelper.ChangeType(myExpectedMethodSignature.ReturnType, element);
                }

                if ((myMatch & MethodSignatureMatch.IncorrectTypeParameters) ==
                    MethodSignatureMatch.IncorrectTypeParameters)
                {
                    // There are no generic Unity methods, so just remove any that are already there
                    myMethodDeclaration.SetTypeParameterList(null);
                }

                return action;
            }

            private Action<ITextControl> ChangeParameters(ISolution solution)
            {
                var model = ClrChangeSignatureModel.CreateModel(myMethodDeclaration.DeclaredElement).NotNull();

                for (var i = 0; i < myExpectedMethodSignature.Parameters.Length; i++)
                {
                    var requiredParameter = myExpectedMethodSignature.Parameters[i];

                    var modelParameter = FindBestMatch(requiredParameter, model, i);
                    if (modelParameter != null)
                    {
                        // If the current index is correct, do nothing. If not, use the original index to find the item to move
                        if (modelParameter.ParameterIndex != i)
                            model.MoveTo(modelParameter.OriginalParameterIndex, i);
                    }
                    else
                    {
                        model.Add(i);
                        modelParameter = model.ChangeSignatureParameters[i];
                    }

                    modelParameter.ParameterName = requiredParameter.Name;
                    modelParameter.ParameterKind = ParameterKind.VALUE;
                    modelParameter.ParameterType = requiredParameter.Type;

                    // Reset everything else
                    modelParameter.IsOptional = false;
                    modelParameter.IsParams = false;
                    modelParameter.IsThis = false;
                    modelParameter.IsVarArg = false;
                }

                for (var i = model.ChangeSignatureParameters.Length - 1;
                    i >= myExpectedMethodSignature.Parameters.Length;
                    i--)
                    model.RemoveAt(i);

                var refactoring = new ChangeSignatureRefactoring(model);
                refactoring.Execute(NullProgressIndicator.Create());

                // Ideally, we would now call InplaceRefactoringsManager.Reset to make sure we didn't have
                // an inplace refactoring highlight. But InplaceRefactoringsManager is internal, so we can't.
                // We don't want a highlight telling us to "apply signature change refactoring" because we
                // just have. The only way to remove it is to fire the Escape action
                return tc =>
                {
                    var highlightingManager = solution.GetComponent<InplaceRefactoringsHighlightingManager>();
                    if (highlightingManager.GetHighlightersForTests(tc).Any())
                    {
                        var actionManager = solution.GetComponent<IActionManager>();
                        var escapeActionHandler = actionManager.Defs.GetActionDef<EscapeActionHandler>();
                        escapeActionHandler.EvaluateAndExecute(actionManager);
                    }
                };
            }

            private ClrChangeSignatureParameter FindBestMatch(ParameterSignature requiredParameter,
                ClrChangeSignatureModel model, int i)
            {
                var parameters = model.ChangeSignatureParameters;

                // Look through all parameters for an exact match
                for (var j = i; j < parameters.Length; j++)
                {
                    if (parameters[j].ParameterName == requiredParameter.Name
                        && Equals(parameters[j].ParameterType, requiredParameter.Type))
                    {
                        return parameters[j];
                    }
                }

                // Now just match type - we'll update name after
                for (var j = i; j < parameters.Length; j++)
                {
                    if (Equals(parameters[j].ParameterType, requiredParameter.Type))
                    {
                        return parameters[j];
                    }
                }

                return null;
            }

            private string GetText(IMethodDeclaration methodDeclaration, MethodSignature signature)
            {
                var language = methodDeclaration.Language;
                var declaredElement = methodDeclaration.DeclaredElement;
                Assertion.AssertNotNull(declaredElement, "declaredElement != null");
                var methodName = DeclaredElementPresenter.Format(language, DeclaredElementPresenter.NAME_PRESENTER,
                    declaredElement);

                switch (myMatch)
                {
                    case MethodSignatureMatch.IncorrectStaticModifier:
                    {
                        var staticTerm = PresentationHelper.GetHelper(language).GetStaticTerm();
                        return signature.IsStatic == true ? $"Make '{methodName}' {staticTerm}" : $"Remove '{staticTerm}' modifier";
                    }
                    case MethodSignatureMatch.IncorrectParameters:
                        return $"Change parameters to '({signature.Parameters.GetParameterList()})'";
                    case MethodSignatureMatch.IncorrectReturnType:
                        return $"Change return type to '{signature.GetReturnTypeName()}'";
                    case MethodSignatureMatch.IncorrectTypeParameters:
                        return "Remove type parameters";

                    // NoMatch, and any combination of flags
                    default:
                        return $"Change signature to '{signature.FormatSignature(methodName.Text)}'";
                }
            }
        }
    }
}