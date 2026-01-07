using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace PackageIncrementer
{
    public class PackageIncrementerWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private string _filterText = "";
        private List<EmbeddedPackageInfo> _embeddedPackages = new List<EmbeddedPackageInfo>();
        private readonly List<(Request request, Action<Request> onComplete)> _pendingRequests =
            new List<(Request, Action<Request>)>();
        private bool _isLoading = false;
        private bool _wasWindowClosed = true;

        [MenuItem("Window/Package Management/Package Incrementer", false, 2101)]
        public static void ShowWindow()
        {
            var window = GetWindow<PackageIncrementerWindow>("Package Incrementer");
            window.minSize = new Vector2(600, 300);
        }

        private void OnEnable()
        {
            _wasWindowClosed = false;
            LoadEmbeddedPackages();
            EditorApplication.update += UpdatePendingRequests;
        }

        private void OnDisable()
        {
            EditorApplication.update -= UpdatePendingRequests;
            _wasWindowClosed = true;

            // 未完了のリクエストをクリア
            _pendingRequests.Clear();
            _isLoading = false;
        }

        private void OnFocus()
        {
            // ウィンドウが閉じられていた場合のみ再読み込み
            if (_wasWindowClosed)
            {
                LoadEmbeddedPackages();
            }
            Repaint();
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawPackageList();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // 実行中はボタンを無効化
            EditorGUI.BeginDisabledGroup(_isLoading);
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                LoadEmbeddedPackages();
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField("Filter:", GUILayout.Width(40));
            _filterText = EditorGUILayout.TextField(_filterText,
                EditorStyles.toolbarSearchField, GUILayout.Width(200));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawPackageList()
        {
            if (_isLoading)
            {
                EditorGUILayout.HelpBox("パッケージ情報を読み込み中...", MessageType.Info);
                return;
            }

            if (_embeddedPackages.Count == 0)
            {
                EditorGUILayout.HelpBox("Embedded パッケージが見つかりませんでした。", MessageType.Info);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            var filteredPackages = _embeddedPackages
                .Where(pkg => string.IsNullOrEmpty(_filterText) ||
                              pkg.packageName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                              pkg.displayName.IndexOf(_filterText, StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var packageInfo in filteredPackages)
            {
                DrawPackageEntry(packageInfo);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawPackageEntry(EmbeddedPackageInfo packageInfo)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(packageInfo.displayName, EditorStyles.boldLabel);

            EditorGUI.indentLevel++;

            EditorGUILayout.LabelField("Package:", packageInfo.packageName, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Version:", packageInfo.currentVersion, EditorStyles.miniLabel);

            if (!packageInfo.isValidVersion)
            {
                EditorGUILayout.HelpBox(
                    "バージョンが無効です。セマンティックバージョニング（例: 1.2.3）に従ってください。",
                    MessageType.Warning);
            }
            else
            {
                DrawVersionButtons(packageInfo);
            }

            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
        }

        private void DrawVersionButtons(EmbeddedPackageInfo packageInfo)
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField("Increment:", GUILayout.Width(70));

            var nextMajor = new SemanticVersion(packageInfo.version.major + 1, 0, 0);
            var nextMinor = new SemanticVersion(packageInfo.version.major, packageInfo.version.minor + 1, 0);
            var nextPatch = new SemanticVersion(packageInfo.version.major, packageInfo.version.minor, packageInfo.version.patch + 1);

            if (GUILayout.Button($"Major ({nextMajor})", GUILayout.Height(24)))
            {
                OnVersionButtonClick(packageInfo, VersionPart.Major, nextMajor.ToString());
            }

            if (GUILayout.Button($"Minor ({nextMinor})", GUILayout.Height(24)))
            {
                OnVersionButtonClick(packageInfo, VersionPart.Minor, nextMinor.ToString());
            }

            if (GUILayout.Button($"Patch ({nextPatch})", GUILayout.Height(24)))
            {
                OnVersionButtonClick(packageInfo, VersionPart.Patch, nextPatch.ToString());
            }

            EditorGUILayout.EndHorizontal();
        }

        private void OnVersionButtonClick(EmbeddedPackageInfo packageInfo, VersionPart part, string newVersionString)
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "バージョンのインクリメント",
                $"{packageInfo.displayName}\nバージョンを {packageInfo.currentVersion} → {newVersionString} に更新しますか？",
                "OK",
                "キャンセル"
            );

            if (confirmed)
            {
                IncrementVersion(packageInfo, part, newVersionString);
            }
        }

        private void LoadEmbeddedPackages()
        {
            // 既に実行中なら何もしない
            if (_isLoading)
                return;

            _isLoading = true;
            _embeddedPackages.Clear();

            var listRequest = Client.List(offlineMode: true, includeIndirectDependencies: false);
            ProcessAsyncRequest(listRequest, (req) =>
            {
                var result = (ListRequest)req;

                foreach (var packageInfo in result.Result)
                {
                    if (packageInfo.source == PackageSource.Embedded)
                    {
                        var embeddedInfo = CreateEmbeddedPackageInfo(packageInfo);
                        if (embeddedInfo != null)
                        {
                            _embeddedPackages.Add(embeddedInfo);
                        }
                    }
                }

                _embeddedPackages.Sort((a, b) =>
                    string.Compare(a.packageName, b.packageName, StringComparison.Ordinal));

                _isLoading = false;
                Repaint();
            });
        }

        private EmbeddedPackageInfo CreateEmbeddedPackageInfo(UnityEditor.PackageManager.PackageInfo packageInfo)
        {
            string packageJsonPath = Path.Combine(packageInfo.resolvedPath, "package.json");

            if (!File.Exists(packageJsonPath))
            {
                Debug.LogWarning($"package.json not found for {packageInfo.name}: {packageJsonPath}");
                return null;
            }

            var info = new EmbeddedPackageInfo
            {
                packageName = packageInfo.name,
                displayName = packageInfo.displayName,
                packageJsonPath = packageJsonPath,
                currentVersion = packageInfo.version
            };

            if (TryParseVersion(packageInfo.version, out var version))
            {
                info.version = version;
                info.isValidVersion = true;
            }
            else
            {
                info.isValidVersion = false;
            }

            return info;
        }

        private bool TryParseVersion(string versionString, out SemanticVersion version)
        {
            version = new SemanticVersion();

            if (string.IsNullOrWhiteSpace(versionString))
                return false;

            var match = Regex.Match(versionString, @"^(\d+)\.(\d+)\.(\d+)");

            if (!match.Success)
                return false;

            if (!int.TryParse(match.Groups[1].Value, out version.major))
                return false;

            if (!int.TryParse(match.Groups[2].Value, out version.minor))
                return false;

            if (!int.TryParse(match.Groups[3].Value, out version.patch))
                return false;

            return true;
        }

        private void IncrementVersion(EmbeddedPackageInfo packageInfo, VersionPart part, string newVersionString)
        {
            if (UpdatePackageJsonVersion(packageInfo.packageJsonPath, newVersionString))
            {
                TryParseVersion(newVersionString, out packageInfo.version);
                packageInfo.currentVersion = newVersionString;

                Debug.Log($"{packageInfo.displayName} のバージョンを {newVersionString} に更新しました。");

                Client.Resolve();

                Repaint();
            }
        }

        private bool UpdatePackageJsonVersion(string packageJsonPath, string newVersion)
        {
            try
            {
                if (!TryReadPackageJson(packageJsonPath, out string content))
                    return false;

                string pattern = @"(""version""\s*:\s*"")[^""]+("")";
                string newContent = Regex.Replace(content, pattern, match =>
                {
                    return match.Groups[1].Value + EscapeJsonString(newVersion) + match.Groups[2].Value;
                });

                if (newContent == content)
                {
                    EditorUtility.DisplayDialog("エラー",
                        "package.json の version フィールドが見つかりませんでした。",
                        "OK");
                    return false;
                }

                File.WriteAllText(packageJsonPath, newContent, Encoding.UTF8);
                AssetDatabase.Refresh();

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to update package.json: {ex.Message}");
                EditorUtility.DisplayDialog("エラー",
                    $"package.json の更新に失敗しました:\n{ex.Message}",
                    "OK");
                return false;
            }
        }

        private bool TryReadPackageJson(string path, out string content)
        {
            content = null;

            try
            {
                if (!File.Exists(path))
                {
                    Debug.LogError($"package.json not found: {path}");
                    return false;
                }

                content = File.ReadAllText(path, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to read package.json: {ex.Message}");
                return false;
            }
        }

        private static string EscapeJsonString(string str)
        {
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void ProcessAsyncRequest(Request request, Action<Request> onComplete)
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
                    if (request.Status == StatusCode.Success)
                    {
                        onComplete?.Invoke(request);
                    }
                    else if (request.Status >= StatusCode.Failure)
                    {
                        Debug.LogError($"Package Manager request failed: {request.Error.message}");
                        EditorUtility.DisplayDialog("エラー",
                            $"パッケージ操作に失敗しました:\n{request.Error.message}",
                            "OK");

                        // エラー時にフラグをリセット
                        _isLoading = false;
                    }

                    _pendingRequests.RemoveAt(i);
                    Repaint();
                }
            }
        }
    }

    [Serializable]
    internal class EmbeddedPackageInfo
    {
        public string packageName;
        public string displayName;
        public string currentVersion;
        public string packageJsonPath;
        public SemanticVersion version;
        public bool isValidVersion;
    }

    [Serializable]
    internal struct SemanticVersion
    {
        public int major;
        public int minor;
        public int patch;

        public SemanticVersion(int major, int minor, int patch)
        {
            this.major = major;
            this.minor = minor;
            this.patch = patch;
        }

        public override string ToString()
        {
            return $"{major}.{minor}.{patch}";
        }
    }

    internal enum VersionPart
    {
        Major,
        Minor,
        Patch
    }
}
