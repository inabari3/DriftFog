# Drift Fog

地表を流れる霧をシーンの一部に配置する、Unity 6.3 / URP 17.3 用のアセットです。空気の速度と霧の密度を体積ごとの三次元格子に保持します。移動するキャラクターは空気を巻き込み、風を受ける角柱の下流では左右交互に発生した渦が霧を運びます。

## 導入

1. DriftFog の GitHub ページで **Code → HTTPS** の URL をコピーし、末尾に `#v1.2.0-preview.3` を付けます。Unity の **Window → Package Management → Package Manager → ＋ → Install package from git URL…** に貼り付けます。Git URL からの導入には Git が必要です。
2. 使用中の URP Asset で **Depth Texture** を有効にします。対象は 3D の Universal Renderer です。カメラ側で深度テクスチャを上書きする場合も有効にしてください。Renderer の **Depth Texture Mode** は **After Opaques** または **Force Prepass** を選びます。
3. Package Manager の **Drift Fog → Samples → Fog Courtyard → Import** でサンプルを追加し、インポート先の **FogCourtyard** シーンを開いて再生します。

URL の書式は次のとおりです。`OWNER/REPOSITORY` の部分に公開先の所有者名とリポジトリ名が入ります。

```text
https://github.com/OWNER/REPOSITORY.git#v1.2.0-preview.3
```

配布リポジトリを手元に展開済みなら、**Install package from disk…** でルートの `package.json` を選んでも導入できます。対応環境は Unity 6000.3 / URP 17.3 の 3D プロジェクトです。

中庭ではランタンが自動で霧を横切ります。初期視点はランタンを斜め上から追尾し、脇へ押し出された霧が後ろへ折り返す様子を観察できます。**View** で中庭全体に切り替え、地面をクリックすると、ランタンがそこへ移動します。左下の **Flow** で流体と解析表現、**Quality** で品質を切り替えます。**H** で操作パネルを隠せます。照明は Linear 色空間で調整しています。

柱の後流を見るには、**Wind** を **steady** に切り替えます。背景の乱流を止め、奥の二本の柱から手前へ 2.5 m/s の風を流します。**Quality** を **High** にすると柱の断面を細かい格子で扱えます。もう一度 **Wind** を押すと元の風に戻ります。

## 自分のシーンで使う

- **霧を置く** — **GameObject → Effects → Drift Fog Volume** で追加するか、付属の **Drift Fog Volume** プレハブをシーンに置きます。Transform の中心が霧の中心、Scale がローカル軸方向の全幅・高さ・奥行きです。正のスケールを使い、下端を地面より少し低くすると地表に馴染みます。Shape から箱型または楕円体を選べます。
- **霧を押し分ける** — キャラクターの腰や移動オブジェクトに **Fog Interactor** を追加します。**Radius** はワールド単位の半径、**Strength** は作用の強度です。流体モードでは物体の運動が格子に残り、**Recovery** と風速が航跡の埋まり方に影響します。解析モードの航跡の寿命は **Trail Lifetime** で設定できます。
- **壁や柱に沿わせる** — **Box Collider** と **Fog Obstacle** を同じオブジェクトに追加します。流体が境界を回り込むよう、柱や壁の厚みを格子の一辺より大きく取ります。Trigger の Box Collider も使えます。解析モードでは **Edge Width** が外縁の幅（m）になります。
- **光を入れる** — メインの Directional Light の向きとシャドウが霧に反映されます。霧を暖色で照らしたい Point Light に **Fog Light** を追加し、光の色、Range と **Scattering** を調整します。局所灯はシャドウなしの近似散乱です。
- **見た目を保存する** — **Assets → Create → Drift Fog → Fog Profile** から設定アセットを作成して Volume の **Profile** に割り当てます。付属の **GroundMist**（青灰色の地霧）、**CoastalBank**（濃く流れの速い海霧）、**HighlandVeil**（薄紫の霧）も使えます。

| プロファイル項目 | 調整できる内容 |
| --- | --- |
| Tint / Shade / Density | 光側と陰側の色、光が霧を通過するときの減衰量 |
| Wind / Turbulence | ワールド座標での風速（m/s）、渦の強さ |
| Noise Frequency | ワールド 1 m 当たりのノイズ反復数。小さくすると霧の塊が大きくなる |
| Height Falloff / Edge Softness | 地面付近への集まり方、体積の境界が薄くなる幅 |
| Quality / Max Distance | 最大レイマーチ数、カメラから描画する最大距離 |
| Simulate Fluid / Simulation Rate | 三次元流体の有効化、1 秒あたりの固定更新回数 |
| Vorticity / Recovery | 移流で失われる渦の補強量、霧を周囲の供給密度へ戻す速度（毎秒） |
| Wake Shedding | 風を受ける角柱・移動体の両脇から、交互に速度場へ加える回転の強度。0 で無効、既定は 1 |
| Kinematic Viscosity | 隣接する流体の速度差が伝わる度合い（m²/s）。密度は移流で運ぶ。既定は 0、中庭の GroundMist は 0.016 |
| High Grid Resolution | High の格子の最長辺。88～128 セルを 4 セル刻みで選択。中庭は 128 |

同じプロファイルを参照する体積は変更を共有します。実行時に一つだけ変更する場合は `volume.Profile = Instantiate(volume.Profile)` でコピーを割り当て、使用後にコピーを `Destroy` します。カスタム asmdef から型を使う場合は `DriftFog.Runtime` を参照してください。

### 柱の幅と渦の周期

後流には、格子より細かい境界層の剥離を補う角柱モデルを使います。周期の目安は `D / (0.13 × U)` 秒です。D は流れに直交する障害物の見付幅（m）、U は障害物に対する風速（m/s）です。たとえば幅 0.8 m の正方形の柱に 2.4 m/s の風を当てると、同じ側から渦が出る周期は約 2.56 秒です。速度を加える範囲は柱のすぐ後ろに限り、その先では格子の移流・圧力計算に従います。幅を少なくとも横方向の 3 セル程度に取ると剥離を表現しやすくなります。角柱に用いた係数の参考値は[正方形断面の実験](https://www.cambridge.org/core/journals/journal-of-fluid-mechanics/article/abs/vortex-shedding-behind-a-square-cylinder-in-transonic-flows/B17E724ABACCD4C9B868F047247B24D2)に基づきます。

## 描画負荷と適用範囲

霧は体積の投影範囲だけをレイマーチします。不透明な地形や壁の深度で視線を打ち切り、霧が十分濃い場合は途中で積分を終了します。周期ノイズは同梱の 48³・1 チャンネルのテクスチャから読みます。カメラや霧ごとの全画面バッファは使用しません。

| Quality | 1 ピクセル・1 体積当たりの最大ステップ数 | 流体格子の最長辺 | 立方体 1 個の格子データ量 |
| --- | ---: | ---: | ---: |
| Low | 12 | 40 セル | 約 2.4 MiB |
| Balanced | 24 | 64 セル | 約 9.9 MiB |
| High | 48 | 88 セル（既定）～128 セル | 約 36.1～111 MiB |

データ量は速度・密度・圧力・作業用テクスチャの合計から算出しています。GPU ドライバーの配置・アラインメント分は別途必要です。平たい地霧では短い軸のセル数が減ります。視線が霧を短くかすめる場合は、積分歩数も減ります。

High では風と局所流との移動量の差も格子に保存し、太い霧の帯と細かな濃淡を流れに沿って曲げます。中庭の地霧は 128×20×108 セルで、上表の格子を合わせて約 14.6 MiB です。追加格子を確保できない場合も密度と光の描画を継続します。

流体には Compute Shader と書き込み可能な 3D テクスチャが必要です。非対応 GPU と **Simulate Fluid** がオフのプロファイルでは解析表現を使用します。領域ごとに計算するため、隣り合う体積は互いの速度や密度を交換しません。コライダーは格子解像度で近似されます。広い屋外では、キャラクターの近くや流れを見せたい場所に体積を絞ると、描画とシミュレーションの両方の負荷を下げられます。

自動で維持する流体格子の上限は既定で 4 個です。プレイ開始時に `DriftFogVolume.MaxSimulatedVolumes` で設定できます。0 にするとすべての自動描画が解析表現になります。複数カメラは上限を共有し、可視体積の境界に近いものが優先されます。明示的に `AdvanceSimulation` で進める格子は呼び出し側で管理します。個々の格子が扱う移動体は最大 12、固体は最大 8 です。設定画面の **Active fluid grid** は実際に使用中の解像度を示します。

不透明な地形はカメラの深度に従って霧を遮ります。周囲に渦や晴れ間を作るには Fog Obstacle または Fog Interactor を配置します。透明物と複数体積の前後関係は URP の透明描画順に従います。


## ライセンス

Drift Fog のコード、シェーダー、中庭サンプルは MIT ライセンスで提供します。Unity と URP には各提供元のライセンスが適用されます。[MIT ライセンス全文](LICENSE.md)

