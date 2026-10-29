namespace RigUninstaller.Models;

/// <summary>Columns of the program list that can be sorted. Names match the grid's SortMemberPath values.</summary>
public enum SortColumn
{
    Name,
    Publisher,
    Version,
    Drive,
    Size,
    InstallDate,
    LastUsed,
}
