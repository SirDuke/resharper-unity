﻿using JetBrains.ReSharper.Plugins.Unity.AsmDefNew.Daemon.Errors;
using JetBrains.ReSharper.Plugins.Unity.JsonNew.Psi;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.TestFramework;
using NUnit.Framework;

namespace JetBrains.ReSharper.Plugins.Unity.Tests.AsmDef.Feature.Services.Daemon
{
    [TestUnity]
    [TestFileExtension(".asmdef")]
    public class AsmDefMismatchedFilenameProblemAnalyzerTests : JsonNewHighlightingTestBase<MismatchedAsmDefFilenameWarning>
    {
        protected override PsiLanguageType CompilerIdsLanguage => JsonNewLanguage.Instance;
        protected override string RelativeTestDataPath => $@"AsmDef\Daemon\Stages\Analysis\MismatchedFilename\{Utils.ProductGoldSuffix}";

        [Test] public void Test01() { DoNamedTest(); }
    }
}
