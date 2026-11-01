using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService;

namespace Ryujinx.HLE.HOS.Services.Am
{
    [Service("appletOE")]
    class IApplicationProxyService : IpcService
    {
        public IApplicationProxyService(ServiceCtx context) { }

        [CommandCmif(0)]
        // OpenApplicationProxy(u64, pid, handle<copy>) -> object<nn::am::service::IApplicationProxy>
        public ResultCode OpenApplicationProxy(ServiceCtx context)
        {
            MakeObject(context, new IApplicationProxy(context.Request.HandleDesc.PId));

            return ResultCode.Success;
        }

        [CommandCmif(10)]
        // Switch 2 alias of command 0.
        public ResultCode OpenApplicationProxySwitch2(ServiceCtx context)
        {
            return OpenApplicationProxy(context);
        }
    }
}
