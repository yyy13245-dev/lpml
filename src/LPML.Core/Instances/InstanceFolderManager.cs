using LPML.Core.Utils;

namespace LPML.Core.Instances;

/// <summary>实例文件夹注册表：持久化到 config\folders.json。</summary>
public sealed class InstanceFolderManager
{
    private readonly string _file;
    private readonly string _rootDirectory;

    public List<InstanceFolder> Folders { get; private set; } = new();

    public event EventHandler? FoldersChanged;

    public InstanceFolderManager(string file, string rootDirectory)
    {
        _file = file;
        _rootDirectory = rootDirectory;
    }

    public void Load()
    {
        Folders = Json.Load<List<InstanceFolder>>(_file) ?? new List<InstanceFolder>();
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try { Json.Save(_file, Folders); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>把文件夹路径解析为绝对路径（相对路径基于启动器根目录）。</summary>
    public string Resolve(InstanceFolder folder)
    {
        if (folder is null) return _rootDirectory;
        if (string.IsNullOrWhiteSpace(folder.Path)) return _rootDirectory;
        var text = Environment.ExpandEnvironmentVariables(folder.Path);
        return System.IO.Path.IsPathRooted(text)
            ? text
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(_rootDirectory, text));
    }

    /// <summary>路径是否可用（存在或可创建）。</summary>
    public bool TryResolve(InstanceFolder folder, out string fullPath)
    {
        fullPath = Resolve(folder);
        return !string.IsNullOrWhiteSpace(fullPath);
    }

    public InstanceFolder Create(string name, string path, bool isDefault = false)
    {
        var folder = new InstanceFolder
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Path = path,
            IsDefault = isDefault,
            CreatedAt = DateTimeOffset.Now
        };
        if (isDefault)
            foreach (var other in Folders) other.IsDefault = false;

        Folders.Add(folder);
        Save();
        FoldersChanged?.Invoke(this, EventArgs.Empty);
        return folder;
    }

    public void Update(InstanceFolder folder)
    {
        Save();
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>只从注册表移除，不动磁盘上的文件。</summary>
    public void Delete(InstanceFolder folder)
    {
        Folders.Remove(folder);
        if (folder.IsDefault && Folders.Count > 0) Folders[0].IsDefault = true;
        Save();
        FoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    public InstanceFolder? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : Folders.FirstOrDefault(f => f.Id == id);

    public InstanceFolder? Default()
        => Folders.FirstOrDefault(f => f.IsDefault) ?? Folders.FirstOrDefault();

    /// <summary>确保目录结构存在（versions / libraries / assets / mods / saves）。</summary>
    public void EnsureLayout(InstanceFolder folder)
    {
        var root = Resolve(folder);
        foreach (var sub in new[] { "versions", "libraries", "assets", "mods", "saves", "config", "resourcepacks", "shaderpacks" })
            FileHelper.EnsureDirectory(System.IO.Path.Combine(root, sub));
    }
}
