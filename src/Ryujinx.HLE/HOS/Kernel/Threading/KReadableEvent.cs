using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.Horizon.Common;

namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    class KReadableEvent : KSynchronizationObject
    {
        private static int _idCounter;
        public int EventId { get; } = System.Threading.Interlocked.Increment(ref _idCounter);

        public static readonly System.Collections.Concurrent.ConcurrentBag<KReadableEvent> All = new();

        private bool _signaled;

        public KReadableEvent(KernelContext context) : base(context)
        {
            All.Add(this);
            Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.KernelSvc,
                $"KReadableEvent created id={EventId} hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}");
        }

        public override void Signal()
        {
            KernelContext.CriticalSection.Enter();

            if (!_signaled)
            {
                _signaled = true;

                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.KernelSvc,
                    $"TRACEEVENT signal readable hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)} thread={KernelStatic.GetCurrentThread()?.ThreadUid ?? 0}");

                base.Signal();
            }

            KernelContext.CriticalSection.Leave();
        }

        public Result Clear()
        {
            _signaled = false;

            return Result.Success;
        }

        public Result ClearIfSignaled()
        {
            Result result;

            KernelContext.CriticalSection.Enter();

            if (_signaled)
            {
                _signaled = false;

                result = Result.Success;
            }
            else
            {
                result = KernelResult.InvalidState;
            }

            KernelContext.CriticalSection.Leave();

            return result;
        }

        public override bool IsSignaled()
        {
            return _signaled;
        }
    }
}
