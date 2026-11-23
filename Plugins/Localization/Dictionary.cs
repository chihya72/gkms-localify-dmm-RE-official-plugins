namespace Gkms.Plugins.Localization;

// Shared parsing keeps capture and translation on the same original DMM rules.
public sealed class TextDictionary : Gkms.Sdk.DmmTextDictionary
{
    public IReadOnlyList<(string File, Gkms.Sdk.DmmMasterTable Table)> MasterTables { get; }
    public TextDictionary(string root) : this(root, new List<(string, Gkms.Sdk.DmmMasterTable)>()) { }
    private TextDictionary(string root, List<(string, Gkms.Sdk.DmmMasterTable)> tables)
        : base(root, true, (file, table) => tables.Add((file, table))) => MasterTables = tables;
}
