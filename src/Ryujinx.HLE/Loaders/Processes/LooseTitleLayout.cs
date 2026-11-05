// SPDX-License-Identifier: MIT
using System.IO;

namespace Ryujinx.HLE.Loaders.Processes
{
    /// <summary>
    /// Resolves the exeFs/RomFS paths of a loose (unpacked) title folder.
    /// <para>
    /// Two shapes exist: dumps that keep executables at the root, and dumps that
    /// use the <c>exefs</c> subfolder next to <c>romfs</c> (the shape Switch 2
    /// loose dumps and the title extractor produce). Resolving this in one place
    /// keeps the headless path from handing the wrong folder to the loader,
    /// which fails later as a misleading kernel error.
    /// </para>
    /// </summary>
    public static class LooseTitleLayout
    {
        public const string RomFsDirectoryName = "romfs";

        /// <summary>
        /// Resolves the executable folder and optional RomFS folder for a loose title.
        /// </summary>
        /// <param name="rootPath">Folder the user selected.</param>
        /// <returns>
        /// The folder holding <c>main</c>/<c>main.npdm</c>, and the RomFS folder when
        /// one exists (<c>null</c> when the title has no RomFS).
        /// </returns>
        public static (string ExeFsPath, string RomFsPath) Resolve(string rootPath)
        {
            string rootRomFs = Path.Combine(rootPath, RomFsDirectoryName);
            string exeFsSubfolder = Path.Combine(rootPath, UnpackedTitleLayout.ExeFsDirectoryName);

            if (Directory.Exists(exeFsSubfolder))
            {
                // exefs and romfs are siblings inside the title folder.
                return (exeFsSubfolder, Directory.Exists(rootRomFs) ? rootRomFs : null);
            }

            // Legacy shape: the executables sit at the root, so the RomFS of the
            // same title lives in the parent folder (a sibling of the root).
            string parentRomFs = Path.Combine(Path.GetDirectoryName(rootPath) ?? rootPath, RomFsDirectoryName);

            return (rootPath, Directory.Exists(parentRomFs) ? parentRomFs : null);
        }
    }
}
