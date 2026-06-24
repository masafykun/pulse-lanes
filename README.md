# 🎵 PULSE LANES

> ビートに合わせてネオンハイウェイを駆け抜ける、3Dリズムタップアーケード。

4レーンのネオンハイウェイを流れてくる光るノーツを、ビートに合わせてタップで叩くリズムゲームです。PERFECT / GOOD のタイミング判定、コンボ倍率、FEVER モードを備え、連続ヒットが伸びるほどヒット音がペンタトニックスケールを駆け上がります。Unity 製の WebGL ビルドで、ブラウザから直接プレイできます。

![Unity](https://img.shields.io/badge/Unity-6000.0.77f1-000000?style=flat-square&logo=unity)
![WebGL](https://img.shields.io/badge/WebGL-990000?style=flat-square&logo=webgl)
![C#](https://img.shields.io/badge/C%23-239120?style=flat-square&logo=csharp)

🔗 **[Live Demo](https://masafykun.github.io/pulse-lanes/)**

---

## 📸 スクリーンショット
![screenshot](screenshot.png)

---

## 🎮 操作方法
| 操作 | 動作 |
|---|---|
| タップ / クリック | レーン上のノーツを叩く |
| ビートに合わせる | PERFECT / GOOD のタイミング判定 |
| 連続ヒット | コンボ倍率の上昇・FEVER モード突入 |

---

## ✨ 特徴
- **4レーン リズムタップ** — ネオンハイウェイを流れるノーツをビートに合わせて叩く
- **タイミング判定** — PERFECT / GOOD でスコアが変化
- **コンボ & FEVER** — 連続ヒットで倍率が上がり、FEVER モードに突入
- **メロディックなヒット音** — 連続数に応じてペンタトニックスケールを駆け上がる打音

---

## 🛠️ 技術スタック
| カテゴリ | 技術 |
|---|---|
| ゲームエンジン | Unity 6000.0.77f1 |
| 言語 | C#（`src/` 配下） |
| ビルド | WebGL |
| 配信 | GitHub Pages |

---

## 🚀 セットアップ

```bash
# WebGL ビルドはブラウザで直接プレイ可能
# Live Demo: https://masafykun.github.io/pulse-lanes/

# ローカルで動かす場合（CORS 回避のため簡易サーバー経由で開く）
python3 -m http.server 8000
# ブラウザで http://localhost:8000/ を開く
```

C# ソースは `src/` ディレクトリにあります。Unity（6000.0.77f1）でプロジェクトとして開けます。

---

## ライセンス

このリポジトリには現時点で LICENSE ファイルが含まれていません。再利用を検討される場合は、リポジトリ作者までお問い合わせください。

© 2026 masafykun (https://github.com/masafykun)
