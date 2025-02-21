using System;
using System.Threading;
using JetBrains.Debugger.Model.Plugins.Unity;
using JetBrains.Debugger.Worker.SessionStartup;
using JetBrains.Lifetimes;
using JetBrains.Util;
using Mono.Debugging.Autofac;
using Mono.Debugging.Client;
using Mono.Debugging.Client.DebuggerOptions;
using Mono.Debugging.Soft;

namespace JetBrains.Debugger.Worker.Plugins.Unity.SessionStartup
{
    [DebuggerGlobalComponent]
    public class IosUsbStartInfoHandler : UnityStartInfoHandlerBase<UnityIosUsbStartInfo>
    {
        private readonly Lifetime myLifetime;
        private readonly ILogger myLogger;

        public IosUsbStartInfoHandler(Lifetime lifetime, ILogger logger)
            : base(SoftDebuggerType.Instance)
        {
            myLifetime = lifetime;
            myLogger = logger;
        }

        protected override IDebuggerSessionStarter GetSessionStarter(UnityIosUsbStartInfo iosUsbStartInfo,
                                                                     IDebuggerSessionOptions debuggerSessionOptions)
        {
            var softDebuggerStartInfo = CreateSoftDebuggerStartInfo(iosUsbStartInfo);
            return new IosUsbSessionStarter(myLifetime, iosUsbStartInfo, softDebuggerStartInfo, debuggerSessionOptions,
                myLogger);
        }

        private class IosUsbSessionStarter : DebuggerSessionStarterBase
        {
            private readonly Lifetime myLifetime;
            private readonly UnityIosUsbStartInfo myIOSUsbStartInfo;
            private readonly SoftDebuggerStartInfo mySoftDebuggerStartInfo;
            private readonly ILogger myLogger;

            public IosUsbSessionStarter(Lifetime lifetime,
                                        UnityIosUsbStartInfo iosUsbStartInfo,
                                        SoftDebuggerStartInfo softDebuggerStartInfo,
                                        IDebuggerSessionOptions evaluationOptions,
                                        ILogger logger)
                : base(evaluationOptions)
            {
                myLifetime = lifetime;
                myIOSUsbStartInfo = iosUsbStartInfo;
                mySoftDebuggerStartInfo = softDebuggerStartInfo;
                myLogger = logger;
            }

            protected override void DoStartSession(IDebuggerSession session, IDebuggerSessionOptions options)
            {
                StartUsbmuxdProxy();
                session.Run(mySoftDebuggerStartInfo, options);
            }

            private void StartUsbmuxdProxy()
            {
                try
                {
                    Usbmuxd.Setup(myIOSUsbStartInfo.IosSupportPath);
                    Usbmuxd.StartUsbmuxdListenThread();

                    var found = WaitForDevice(myIOSUsbStartInfo.IosDeviceId);

                    // This shouldn't happen. Log it and let everything else continue to fail
                    if (!found) myLogger.Error("Unable to find device");

                    // Create a proxy between our local port (12000) and the port on the device (56000). These numbers
                    // are hardcoded, and reflect Unity's own values
                    var localPort = (ushort) myIOSUsbStartInfo.MonoPort;
                    if (!Usbmuxd.StartIosProxy(localPort, 56000, myIOSUsbStartInfo.IosDeviceId))
                    {
                        myLogger.Error("StartIosProxy returned false");
                        Usbmuxd.StopUsbmuxdListenThread();
                        Usbmuxd.Shutdown();
                        return;
                    }

                    myLifetime.OnTermination(() => ShutdownProxy(localPort));
                }
                catch (Exception ex)
                {
                    myLogger.Error(ex);
                    throw;
                }
            }

            private bool WaitForDevice(string deviceId)
            {
                // There is a potential race condition with starting the proxy thread before the listen thread has
                // discovered the devices. Make sure our device ID is found
                var retries = 0;
                bool found;
                while ((found = CanFindDevice(deviceId)) == false && retries < 10)
                {
                    myLogger.Info("Cannot find device. Sleeping for 10ms");
                    Thread.Sleep(10);
                    retries++;
                }

                return found;
            }

            private bool CanFindDevice(string deviceId)
            {
                var deviceCount = Usbmuxd.UsbmuxdGetDeviceCount();

                myLogger.Trace($"UsbmuxdGetDeviceCount: {deviceCount}");
                for (uint i = 0; i < deviceCount; i++)
                {
                    if (Usbmuxd.UsbmuxdGetDevice(i, out var device))
                    {
                        myLogger.Trace($"UsbmuxdGetDevice({i}): {device.udid}");
                        if (device.udid == deviceId)
                            return true;
                    }
                }

                return false;
            }

            private void ShutdownProxy(ushort localPort)
            {
                try
                {
                    // Note that during shutdown, usbmuxd outputs an empty line with an [Error] marker. It's
                    // harmless and can be ignored
                    Usbmuxd.StopIosProxy(localPort);
                    Usbmuxd.StopUsbmuxdListenThread();
                    Usbmuxd.Shutdown();
                }
                catch (Exception e)
                {
                    myLogger.Error(e);
                    throw;
                }
            }
        }
    }
}