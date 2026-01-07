# Unity Dev Tools

## Package Overrider

Unity Package Managerで管理されているパッケージを、ローカルディレクトリのパッケージに一時的に差し替えるためのUnity Editor拡張です。

### インストール
Packge Managerの「Add package from git URL...」オプションを使用して、以下のURLからインストールしてください。

```
https://github.com/amamagi/UnityDevTools.git?path=Packages/PackageOverrider
```

### 使い方

`Window > Package Management > Package Overrider` メニューからウィンドウを開き、差し替えたいパッケージにチェックを入れ、ローカルディレクトリのパスを指定して「Apply Changes」ボタンを押します。

## Package Incrementer

Embeddedパッケージのバージョンを簡単にインクリメントできるUnity Editor拡張です。セマンティックバージョニング（Major.Minor.Patch）に準拠したバージョン管理を支援します。

### インストール
Packge Managerの「Add package from git URL...」オプションを使用して、以下のURLからインストールしてください。

```
https://github.com/amamagi/UnityDevTools.git?path=Packages/PackageIncrementer
```

### 使い方

`Window > Package Management > Package Incrementer` メニューからウィンドウを開き、Embeddedパッケージのリストから対象パッケージを選択し、Major、Minor、Patchいずれかのボタンをクリックしてバージョンをインクリメントします。

- **Major**: 互換性のない変更時にインクリメント（例：1.2.3 → 2.0.0）
- **Minor**: 後方互換性のある機能追加時にインクリメント（例：1.2.3 → 1.3.0）
- **Patch**: 後方互換性のあるバグ修正時にインクリメント（例：1.2.3 → 1.2.4）