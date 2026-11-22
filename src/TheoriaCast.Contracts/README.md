# TheoriaCast.Contracts

The shared C# data types describe a scene, its models and inputs, actions, playback state and renderer reports. A `StudioSnapshot` groups these into one message. The package supplies the data exchanged by applications. TheoriaCast.Music can be built independently.

シーン、モデルと入力、アクション、再生状態、レンダラーの報告を表す共通の C# 型です。`StudioSnapshot` にそれらをまとめて受け渡せます。Music は単独でビルドできます。

## Unity

In Package Manager, choose **Install package from git URL** and paste the following URL. Unity の Package Manager で「Git URL からパッケージを追加」を選び、次の URL を入力してください。

```text
https://github.com/egumaru/TheoriaCast.git?path=/src/TheoriaCast.Contracts#contracts-v1.0.0
```

Use `TheoriaCast.Contracts` as the namespace and assembly name. If your scripts have their own assembly definition, add a reference to this assembly. 独自の asmdef を使う場合は、アセンブリ参照に `TheoriaCast.Contracts` を追加します。

## .NET

Clone the repository and include the same source file in the consuming project. For a project located alongside the cloned `TheoriaCast` directory, add the following to its project file. Adjust the relative path to match your checkout. 同じソースを .NET プロジェクトにも取り込めます。下の例は利用側のプロジェクトとクローンした `TheoriaCast` が隣り合う配置です。

```xml
<ItemGroup>
  <Compile Include="../TheoriaCast/src/TheoriaCast.Contracts/Runtime/StudioContracts.cs"
           Link="Contracts/StudioContracts.cs" />
</ItemGroup>
```

The types use public fields so Unity can serialize them. With `System.Text.Json`, enable `IncludeFields` for both writing and reading. Unity の `JsonUtility` とやり取りするため、.NET 側ではシリアライズ・読み込みの両方に `IncludeFields` を指定します。

```csharp
using System.Text.Json;
using TheoriaCast.Contracts;

var options = new JsonSerializerOptions { IncludeFields = true };
var scene = new SceneState { revision = 1 };
var json = JsonSerializer.Serialize(scene, options);
var restored = JsonSerializer.Deserialize<SceneState>(json, options);
```

Licensed under [Apache License 2.0](LICENSE.md).
