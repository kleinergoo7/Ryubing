// Behavior ported from Eglantine (Eden fork) Switch 2 support; see NX2_NOTES.md there.
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Sf;

namespace Ryujinx.Horizon.Sdk.Audio.Detail
{
    // Switch 2 aud:u entry point: command 2 returns the audren:u-style renderer manager.
    partial class Nx2AudioUserClient : IServiceObject
    {
        private readonly IAudioRendererManager _manager;

        public Nx2AudioUserClient(IAudioRendererManager manager)
        {
            _manager = manager;
        }

        [CmifCommand(2)]
        public Result GetAudioRendererManager(out IAudioRendererManager manager)
        {
            manager = _manager;

            return Result.Success;
        }
    }
}
