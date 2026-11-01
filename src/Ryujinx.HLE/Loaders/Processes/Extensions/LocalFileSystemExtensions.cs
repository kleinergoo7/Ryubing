using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.RomFs;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using System.IO;

namespace Ryujinx.HLE.Loaders.Processes
{
    static class LocalFileSystemExtensions
    {
        public static ProcessResult Load(this LocalFileSystem exeFs, Switch device, string romFsPath = "")
        {
            MetaLoader metaLoader = exeFs.GetNpdm();
            BlitStruct<ApplicationControlProperty> nacpData = new(1);
            ulong programId = metaLoader.ProgramId;

            device.Configuration.VirtualFileSystem.ModLoader.CollectMods([programId]);

            if (programId != 0)
            {
                ProcessLoaderHelper.EnsureSaveData(device, new ApplicationId(programId), nacpData);
            }

            ProcessResult processResult = exeFs.Load(device, nacpData, metaLoader, 0);

            // Load RomFS. A directory is exposed as a lazy RomFS view.
            if (!string.IsNullOrEmpty(romFsPath))
            {
                if (Directory.Exists(romFsPath))
                {
                    using LocalFileSystem romFs = new(romFsPath);
                    IStorage storage = new RomFsBuilder(romFs).Build();

                    device.Configuration.VirtualFileSystem.SetRomFs(processResult.ProcessId, storage.AsStream());
                }
                else
                {
                    device.Configuration.VirtualFileSystem.LoadRomFs(processResult.ProcessId, romFsPath);
                }
            }

            return processResult;
        }
    }
}
