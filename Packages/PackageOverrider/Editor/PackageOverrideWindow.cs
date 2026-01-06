using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace PackageOverrider
{
    /// <summary>
    /// manifest.jsonのパッケージソースをローカルパスにオーバーライドするEditorWindow
    /// </summary>
    public class PackageOverriderWindow : EditorWindow
    {
        private const string ManifestPath = "Packages/manifest.json";
        private const string SettingsPath = "UserSettings/PackageOverriderSettings.json";

        private Vector2 _scrollPosition;
        private string _filterText = "";
        private PackageOverrideData _data;
        private bool _hasUnsavedChanges;

        private readonly List<(UnityEditor.PackageManager.Requests.Request request, Action<UnityEditor.PackageManager.Requests.Request> onComplete)> _pendingRequests = new List<(UnityEditor.PackageManager.Requests.Request, Action<UnityEditor.PackageManager.Requests.Request>)>();
        private Dictionary<string, UnityEditor.PackageManager.PackageInfo> _packageInfoCache = new Dictionary<string, UnityEditor.PackageManager.PackageInfo>();

        [MenuItem("Window/Package Management/Package Overrider", false, 2100)]
        public static void ShowWindow()
        {
            var window = GetWindow<PackageOverriderWindow>("Package Overrider");
            window.minSize = new Vector2(500, 300);
        }

        private void OnEnable()
        {
            LoadSettings();
            LoadPackageList();
            EditorApplication.update += UpdatePendingRequests;
        }

        private void OnDisable()
        {
            EditorApplication.update -= UpdatePendingRequests;
        }

        private void OnFocus()
        {
            LoadPackageList();
            Repaint();
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawPackageList();
            DrawFooter();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                LoadPackageList();
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField("Filter:", GUILayout.Width(40));
            _filterText = EditorGUILayout.TextField(_filterText, EditorStyles.toolbarSearchField, GUILayout.Width(200));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawPackageList()
        {
            if (_packageInfoCache == null || _packageInfoCache.Count == 0)
            {
                EditorGUILayout.HelpBox("パッケージ情報を読み込めませんでした。", MessageType.Error);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            var filteredPackages = _packageInfoCache.Values
                .Where(pkg => !pkg.name.StartsWith("com.unity.modules."))
                .Where(pkg => string.IsNullOrEmpty(_filterText) ||
                              pkg.name.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(pkg => pkg.name);

            foreach (var packageInfo in filteredPackages)
            {
                DrawPackageEntry(packageInfo);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawPackageEntry(UnityEditor.PackageManager.PackageInfo packageInfo)
        {
            var entry = GetOrCreateEntry(packageInfo.name);
            var state = GetPackageState(entry);

            string currentSource = GetPackageSourceString(packageInfo);
            if (string.IsNullOrEmpty(entry.originalSource))
            {
                entry.originalSource = currentSource;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            DrawPackageHeader(packageInfo.name, state);
            EditorGUI.indentLevel++;
            DrawPackageInfo(entry, currentSource, state);
            DrawPackageActions(entry, state);
            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
        }

        private string GetPackageSourceString(UnityEditor.PackageManager.PackageInfo packageInfo)
        {
            switch (packageInfo.source)
            {
                case UnityEditor.PackageManager.PackageSource.Registry:
                case UnityEditor.PackageManager.PackageSource.BuiltIn:
                    return packageInfo.version;
                case UnityEditor.PackageManager.PackageSource.Embedded:
                    return $"Embedded: {packageInfo.resolvedPath}";
                case UnityEditor.PackageManager.PackageSource.Local:
                    return $"file:{packageInfo.resolvedPath}";
                case UnityEditor.PackageManager.PackageSource.Git:
                    return packageInfo.packageId.Split('@').Length > 1 ? packageInfo.packageId.Split('@')[1] : packageInfo.packageId;
                default:
                    return packageInfo.version;
            }
        }

        private void DrawPackageHeader(string packageName, PackageState state)
        {
            string displayName = packageName;

            switch (state)
            {
                case PackageState.EmbeddedActive:
                    displayName += " (Embedded)";
                    break;
                case PackageState.EmbeddedDisabled:
                    displayName += " (Embedded - Disabled)";
                    break;
                case PackageState.OverrideActive:
                    displayName += " (Override)";
                    break;
            }

            EditorGUILayout.LabelField(displayName, EditorStyles.boldLabel);
        }

        private void DrawPackageInfo(PackageOverrideEntry entry, string currentSource, PackageState state)
        {
            if (!string.IsNullOrEmpty(entry.originalSource))
            {
                EditorGUILayout.LabelField("Original:", entry.originalSource, EditorStyles.miniLabel);
            }
            else if (state == PackageState.PackageCache)
            {
                EditorGUILayout.LabelField("Current:", currentSource, EditorStyles.miniLabel);
            }

            switch (state)
            {
                case PackageState.OverrideActive:
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Path:", GUILayout.Width(40));
                    string newPath = EditorGUILayout.TextField(entry.overridePath);
                    if (newPath != entry.overridePath)
                    {
                        entry.overridePath = newPath;
                        _hasUnsavedChanges = true;
                    }

                    if (GUILayout.Button("...", GUILayout.Width(30)))
                    {
                        string selectedPath = EditorUtility.OpenFolderPanel(
                            "Select Package Folder",
                            string.IsNullOrEmpty(entry.overridePath) ? "" : entry.overridePath,
                            "");

                        if (!string.IsNullOrEmpty(selectedPath))
                        {
                            entry.overridePath = selectedPath;
                            _hasUnsavedChanges = true;
                        }
                    }
                    EditorGUILayout.EndHorizontal();

                    if (!string.IsNullOrEmpty(entry.overridePath))
                    {
                        string packageJsonPath = Path.Combine(entry.overridePath, "package.json");
                        if (!File.Exists(packageJsonPath))
                        {
                            EditorGUILayout.HelpBox("指定されたパスに package.json が見つかりません。", MessageType.Warning);
                        }
                    }
                    break;

                case PackageState.EmbeddedActive:
                case PackageState.EmbeddedDisabled:
                    string embeddedPath = $"Packages/{entry.packageName}";
                    string statusText = state == PackageState.EmbeddedActive ? "" : " (package.json.disabled)";
                    EditorGUILayout.LabelField("Location:", embeddedPath + statusText, EditorStyles.miniLabel);
                    break;
            }
        }

        private void DrawPackageActions(PackageOverrideEntry entry, PackageState state)
        {
            switch (state)
            {
                case PackageState.PackageCache:
                    DrawPackageCacheActions(entry);
                    break;
                case PackageState.OverrideActive:
                    DrawOverrideActiveActions(entry);
                    break;
                case PackageState.EmbeddedActive:
                    DrawEmbeddedActiveActions(entry);
                    break;
                case PackageState.EmbeddedDisabled:
                    DrawEmbeddedDisabledActions(entry);
                    break;
            }
        }

        private void DrawPackageCacheActions(PackageOverrideEntry entry)
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Embed to Packages/", GUILayout.Height(20)))
            {
                EmbedPackage(entry);
            }

            if (GUILayout.Button("Enable Override", GUILayout.Height(20)))
            {
                entry.isOverridden = true;
                _hasUnsavedChanges = true;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawOverrideActiveActions(PackageOverrideEntry entry)
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Disable Override", GUILayout.Height(20)))
            {
                entry.isOverridden = false;
                _hasUnsavedChanges = true;
            }

            if (GUILayout.Button("Embed to Packages/", GUILayout.Height(20)))
            {
                EmbedPackage(entry);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawEmbeddedActiveActions(PackageOverrideEntry entry)
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Disable Embedded", GUILayout.Height(20)))
            {
                DisableEmbeddedPackage(entry);
            }

            if (GUILayout.Button("Remove from Packages/", GUILayout.Height(20)))
            {
                RemoveEmbeddedPackage(entry);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawEmbeddedDisabledActions(PackageOverrideEntry entry)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("manifest.json version:", GUILayout.Width(140));
            string newVersion = EditorGUILayout.TextField(entry.originalSource ?? "");
            if (GUILayout.Button("Update", GUILayout.Width(60)))
            {
                UpdateManifestVersion(entry, newVersion);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Enable Embedded", GUILayout.Height(20)))
            {
                EnableEmbeddedPackage(entry);
            }

            if (GUILayout.Button("Remove from Packages/", GUILayout.Height(20)))
            {
                RemoveEmbeddedPackage(entry);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUI.BeginDisabledGroup(!_hasUnsavedChanges);
            if (GUILayout.Button("Apply Changes", GUILayout.Width(120), GUILayout.Height(25)))
            {
                ApplyChanges();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
        }

        private void LoadPackageList()
        {
            var listRequest = UnityEditor.PackageManager.Client.List(true, false);

            while (!listRequest.IsCompleted)
            {
                System.Threading.Thread.Sleep(10);
            }

            if (listRequest.Status == UnityEditor.PackageManager.StatusCode.Success)
            {
                _packageInfoCache.Clear();
                foreach (var packageInfo in listRequest.Result)
                {
                    _packageInfoCache[packageInfo.name] = packageInfo;
                }

                UpdateEntriesFromPackageInfo();
            }
            else
            {
                Debug.LogError($"Failed to load package list: {listRequest.Error?.message}");
            }
        }

        private void UpdateEntriesFromPackageInfo()
        {
            foreach (var entry in _data.entries)
            {
                if (_packageInfoCache.TryGetValue(entry.packageName, out var packageInfo))
                {
                    entry.isEmbedded = packageInfo.source == UnityEditor.PackageManager.PackageSource.Embedded;

                    if (entry.isEmbedded)
                    {
                        string packageJsonPath = Path.Combine($"Packages/{entry.packageName}", "package.json");
                        entry.isEmbeddedEnabled = File.Exists(packageJsonPath);
                    }
                    else
                    {
                        entry.isEmbeddedEnabled = false;
                    }
                }
                else
                {
                    entry.isEmbedded = false;
                    entry.isEmbeddedEnabled = false;
                }
            }
        }

        private void ApplyChanges()
        {
            try
            {
                string content = File.ReadAllText(ManifestPath, Encoding.UTF8);
                bool manifestModified = false;

                foreach (var entry in _data.entries)
                {
                    if (!_packageInfoCache.ContainsKey(entry.packageName))
                        continue;

                    var state = GetPackageState(entry);
                    if (state == PackageState.EmbeddedActive)
                    {
                        continue;
                    }

                    string newSource;
                    if (entry.isOverridden && !string.IsNullOrEmpty(entry.overridePath))
                    {
                        newSource = "file:" + entry.overridePath.Replace("\\", "/");
                    }
                    else if (!entry.isOverridden && !string.IsNullOrEmpty(entry.originalSource))
                    {
                        newSource = entry.originalSource;
                    }
                    else
                    {
                        continue;
                    }

                    string pattern = $@"(""{Regex.Escape(entry.packageName)}""\s*:\s*"")[^""]+("")";
                    content = Regex.Replace(content, pattern, $"$1{EscapeJsonString(newSource)}$2");
                    manifestModified = true;
                }

                if (manifestModified)
                {
                    File.WriteAllText(ManifestPath, content, Encoding.UTF8);
                }

                SaveSettings();
                UnityEditor.PackageManager.Client.Resolve();

                _hasUnsavedChanges = false;
                LoadPackageList();

                Debug.Log("Package overrides applied successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to apply changes: {ex.Message}");
                EditorUtility.DisplayDialog("Error", $"変更の適用に失敗しました:\n{ex.Message}", "OK");
            }
        }

        private static string EscapeJsonString(string str)
        {
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                    _data = JsonUtility.FromJson<PackageOverrideData>(json);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to load settings: {ex.Message}");
            }

            _data ??= new PackageOverrideData();
        }

        private void SaveSettings()
        {
            try
            {
                string directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonUtility.ToJson(_data, true);
                File.WriteAllText(SettingsPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to save settings: {ex.Message}");
            }
        }

        private PackageOverrideEntry GetOrCreateEntry(string packageName)
        {
            var entry = _data.entries.Find(e => e.packageName == packageName);
            if (entry == null)
            {
                entry = new PackageOverrideEntry { packageName = packageName };
                _data.entries.Add(entry);
            }
            return entry;
        }

        private PackageState GetPackageState(PackageOverrideEntry entry)
        {
            if (entry.isEmbedded && entry.isEmbeddedEnabled)
                return PackageState.EmbeddedActive;

            if (entry.isEmbedded && !entry.isEmbeddedEnabled)
                return PackageState.EmbeddedDisabled;

            if (entry.isOverridden && !string.IsNullOrEmpty(entry.overridePath))
                return PackageState.OverrideActive;

            return PackageState.PackageCache;
        }

        private void ProcessAsyncRequest(UnityEditor.PackageManager.Requests.Request request, Action<UnityEditor.PackageManager.Requests.Request> onComplete)
        {
            _pendingRequests.Add((request, onComplete));
        }

        private void UpdatePendingRequests()
        {
            for (int i = _pendingRequests.Count - 1; i >= 0; i--)
            {
                var (request, onComplete) = _pendingRequests[i];

                if (request.IsCompleted)
                {
                    if (request.Status == UnityEditor.PackageManager.StatusCode.Success)
                    {
                        onComplete?.Invoke(request);
                    }
                    else if (request.Status >= UnityEditor.PackageManager.StatusCode.Failure)
                    {
                        Debug.LogError($"Package Manager request failed: {request.Error.message}");
                        EditorUtility.DisplayDialog("Error", $"パッケージ操作に失敗しました:\n{request.Error.message}", "OK");
                    }

                    _pendingRequests.RemoveAt(i);
                    Repaint();
                }
            }
        }

        private void EmbedPackage(PackageOverrideEntry entry)
        {
            var state = GetPackageState(entry);
            if (state == PackageState.EmbeddedActive || state == PackageState.EmbeddedDisabled)
            {
                EditorUtility.DisplayDialog("Warning", $"{entry.packageName} は既に Embedded package です。", "OK");
                return;
            }

            var request = UnityEditor.PackageManager.Client.Embed(entry.packageName);
            ProcessAsyncRequest(request, (req) =>
            {
                entry.isEmbedded = true;
                entry.isEmbeddedEnabled = true;
                _hasUnsavedChanges = true;
                SaveSettings();
                Debug.Log($"{entry.packageName} を Embedded package として Packages/ にコピーしました。");
            });
        }

        private void RemoveEmbeddedPackage(PackageOverrideEntry entry)
        {
            if (!EditorUtility.DisplayDialog("Confirm Delete",
                $"Packages/{entry.packageName} を削除しますか?\nこの操作は元に戻せません。",
                "削除", "キャンセル"))
            {
                return;
            }

            var request = UnityEditor.PackageManager.Client.Remove(entry.packageName);
            ProcessAsyncRequest(request, (req) =>
            {
                entry.isEmbedded = false;
                entry.isEmbeddedEnabled = false;
                _hasUnsavedChanges = true;
                SaveSettings();
                Debug.Log($"{entry.packageName} を削除しました。manifest.json の定義があれば再取得されます。");
            });
        }

        private bool EnableEmbeddedPackage(PackageOverrideEntry entry)
        {
            string packageJsonPath = Path.Combine($"Packages/{entry.packageName}", "package.json");
            string disabledPath = packageJsonPath + ".disabled";

            if (!File.Exists(disabledPath))
            {
                EditorUtility.DisplayDialog("Error", "package.json.disabled が見つかりません。", "OK");
                return false;
            }

            File.Move(disabledPath, packageJsonPath);
            entry.isEmbeddedEnabled = true;
            _hasUnsavedChanges = true;

            AssetDatabase.Refresh();
            UnityEditor.PackageManager.Client.Resolve();
            Debug.Log($"{entry.packageName} の Embedded package を有効化しました。");
            return true;
        }

        private bool DisableEmbeddedPackage(PackageOverrideEntry entry)
        {
            string packageJsonPath = Path.Combine($"Packages/{entry.packageName}", "package.json");
            string disabledPath = packageJsonPath + ".disabled";

            if (!File.Exists(packageJsonPath))
            {
                EditorUtility.DisplayDialog("Error", "package.json が見つかりません。", "OK");
                return false;
            }

            File.Move(packageJsonPath, disabledPath);
            entry.isEmbeddedEnabled = false;
            _hasUnsavedChanges = true;

            AssetDatabase.Refresh();
            UnityEditor.PackageManager.Client.Resolve();
            Debug.Log($"{entry.packageName} の Embedded package を無効化しました。manifest.json の定義が有効になります。");
            return true;
        }


        private void UpdateManifestVersion(PackageOverrideEntry entry, string newVersion)
        {
            if (string.IsNullOrWhiteSpace(newVersion))
            {
                EditorUtility.DisplayDialog("Error", "バージョンを入力してください。", "OK");
                return;
            }

            try
            {
                string content = File.ReadAllText(ManifestPath, Encoding.UTF8);
                string pattern = $@"(""{Regex.Escape(entry.packageName)}""\s*:\s*"")[^""]+("")";
                string replacement = $"$1{EscapeJsonString(newVersion)}$2";

                string newContent = Regex.Replace(content, pattern, replacement);

                if (newContent == content)
                {
                    EditorUtility.DisplayDialog("Warning", $"{entry.packageName} が manifest.json に見つかりませんでした。", "OK");
                    return;
                }

                File.WriteAllText(ManifestPath, newContent, Encoding.UTF8);
                entry.originalSource = newVersion;
                SaveSettings();

                UnityEditor.PackageManager.Client.Resolve();
                LoadPackageList();

                Debug.Log($"{entry.packageName} のバージョンを {newVersion} に変更しました。");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to update manifest version: {ex.Message}");
                EditorUtility.DisplayDialog("Error", $"バージョンの変更に失敗しました:\n{ex.Message}", "OK");
            }
        }
    }

    internal enum PackageState
    {
        PackageCache,
        OverrideActive,
        EmbeddedActive,
        EmbeddedDisabled
    }

    [Serializable]
    internal class PackageOverrideEntry
    {
        public string packageName;
        public string originalSource;
        public string overridePath;
        public bool isOverridden;
        public bool isEmbedded;
        public bool isEmbeddedEnabled;
    }

    [Serializable]
    internal class PackageOverrideData
    {
        public List<PackageOverrideEntry> entries = new List<PackageOverrideEntry>();
    }
}
