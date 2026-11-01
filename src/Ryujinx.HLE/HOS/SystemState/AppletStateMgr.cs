using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy;
using System.Collections.Concurrent;

namespace Ryujinx.HLE.HOS.SystemState
{
    class AppletStateMgr
    {
        public ConcurrentQueue<AppletMessage> Messages { get; }

        public FocusState FocusState { get; private set; }

        public KEvent MessageEvent { get; }

        public IdDictionary AppletResourceUserIds { get; }

        public IdDictionary IndirectLayerHandles { get; }

        public AppletStateMgr(Horizon system)
        {
            Messages = new ConcurrentQueue<AppletMessage>();
            MessageEvent = new KEvent(system.KernelContext);

            AppletResourceUserIds = new IdDictionary();
            IndirectLayerHandles = new IdDictionary();

            // Default to InFocus so nn::applet reports a focused state; 0 is neither
            // InFocus (1) nor OutOfFocus (2) and makes the game skip init paths.
            FocusState = FocusState.InFocus;
        }

        public void SetFocus(bool isFocused)
        {
            FocusState = isFocused ? FocusState.InFocus : FocusState.OutOfFocus;

            Messages.Enqueue(AppletMessage.FocusStateChanged);

            if (isFocused)
            {
                Messages.Enqueue(AppletMessage.ChangeIntoForeground);
            }

            MessageEvent.ReadableEvent.Signal();
        }
    }
}
