using JetBrains.ProjectModel;
using JetBrains.ReSharper.Plugins.Unity.Core.Feature.Caches;
using JetBrains.ReSharper.Plugins.Unity.CSharp.Feature.Services.Navigation.GoToUnityUsages;
using JetBrains.ReSharper.Plugins.Unity.Rider.Protocol;

namespace JetBrains.ReSharper.Plugins.Unity.Rider.CSharp.Feature.Services.Navigation.GoToUnityUsages
{
    [SolutionComponent]
    public class RiderUnityUsagesNotification : UnityUsagesDeferredCachesNotification
    {
        private readonly FrontendBackendHost myFrontendBackendHost;

        public RiderUnityUsagesNotification(FrontendBackendHost frontendBackendHost, DeferredCacheController controller)
            : base(controller)
        {
            myFrontendBackendHost = frontendBackendHost;
        }

        protected override void ShowNotification()
        {
            myFrontendBackendHost.Do(t => t.ShowDeferredCachesProgressNotification());
        }
    }
}