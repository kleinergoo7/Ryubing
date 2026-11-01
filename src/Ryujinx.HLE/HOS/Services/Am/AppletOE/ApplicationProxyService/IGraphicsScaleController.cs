namespace Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService
{
    class IGraphicsScaleController : IpcService
    {
        [CommandCmif(10)]
        // GetCurrentGraphicsScale() -> u32
        public ResultCode GetCurrentGraphicsScale(ServiceCtx context)
        {
            context.ResponseData.Write(10u);

            return ResultCode.Success;
        }
    }
}
