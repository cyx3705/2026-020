namespace HistoryJanus;

/// <summary>
/// Janus 自己的扁平设置读写口（5.14.0）。
/// </summary>
/// <remarks>
/// 此前直接借用宿主的 <c>HistoryVulcan.Core.Storage.ISettingsService</c>。宿主 6.0.0 起那是宿主内部类型，
/// 模块只能用契约程序集白名单里的类型，于是接口搬回本仓：形状不变，实现仍是 <c>ModuleSettings</c>。
/// </remarks>
public interface ISettingsService
{
    string? Get(string key);

    int GetInt(string key, int fallback);

    void Set(string key, string value);

    IReadOnlyList<KeyValuePair<string, string>> All();
}
