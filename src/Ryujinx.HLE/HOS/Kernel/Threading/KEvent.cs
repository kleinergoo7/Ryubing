namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    class KEvent
    {
        public KReadableEvent ReadableEvent { get; private set; }
        public KWritableEvent WritableEvent { get; private set; }

        public KEvent(KernelContext context)
        {
            ReadableEvent = new KReadableEvent(context);
            WritableEvent = new KWritableEvent(context, this);

            Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.KernelSvc, $"KEvent create hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ReadableEvent)}");
        }
    }
}
