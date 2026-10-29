using Microsoft.VisualBasic.FileIO;

namespace RigUninstaller.Services;

/// <summary>Moves things to the Windows Recycle Bin instead of deleting them for good.</summary>
public interface IRecycleBin
{
    void Move(string path, bool isDirectory);
}

/// <summary>
/// Uses the Windows shell for the move. All dialogs stay on (UIOption.AllDialogs) on purpose: when
/// something is too big for the Recycle Bin, Windows asks before deleting it for good instead of doing it silently.
/// </summary>
public sealed class RecycleBin : IRecycleBin
{
    public void Move(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
        }
        else
        {
            FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
        }
    }
}
