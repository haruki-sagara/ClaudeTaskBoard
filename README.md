# ClaudeTaskBoard

動作中の Claude Code セッションと、その**タスク一覧・進捗**、Claude Code からの**確認・通知**を、常に最前面のフロートウィンドウに表示する Windows アプリ（WPF / .NET 10）。

> Anthropic の公式ツールではありません。Claude Code の hooks と transcript を読むだけの非公式ツールです。

| 通常表示 | 展開表示 | ミニ表示 |
|---|---|---|
| ![通常表示](docs/screenshot-dark.png) | ![展開表示](docs/screenshot-expanded.png) | ![ミニ表示](docs/screenshot-compact.png) |

（スクリーンショットは架空のデモデータ）

## 表示内容

- 今週の使用量: 使用率のゲージ、残り %、リセット日時（プランの週次上限。詳細は「データの取り方」）
- セッションごとのカード: タイトル、プロジェクト、状態（思考中 / ツール実行中 / 許可待ち / 完了 / 待機中）、経過時間
- タスクの進捗バー（完了数 / 総数）と、実行中のステップ
- カード右上のシェブロンで全ステップを展開 / 格納（完了 / 実行中 / 未着手のアイコン付き）
- カード右上の × でそのカードを非表示にする（重複したフォークなど）。セッションに新しい動き（状態の更新・transcript の追記）があれば自動で再表示する。非表示はボードを再起動すると解除される
- カードのそれ以外の場所をクリックすると、そのセッションのウィンドウを前面に出す
  - デスクトップアプリ: セッションを別ウィンドウで開いていればそのウィンドウ（タイトル一致で判定）を優先し、なければメインウィンドウ。メインウィンドウ内で該当セッションへ切り替える手段は無いため、その場合はトーストで知らせる
  - CLI: セッションのプロセスから親をたどって端末ウィンドウを探す（Windows では記録された pid が既に終了していることが多く、ベストエフォート）
- デスクトップアプリで**別ウィンドウ（子ウィンドウ）に開いているセッションは常に表示**し、タイトル左にウィンドウアイコンを付ける
  - 「完了・待機中を隠す」がオンでも、4 時間以上更新がなくても消えない
  - hook の状態ファイルが無いセッション（hook 導入前から開いている等）も、ウィンドウタイトルと直近 14 日の transcript の `custom-title` を照合して表示する（状態は「待機中」、タスク一覧・判断待ちは transcript から取得）
- 並び順: 確認待ち → 実行中 → 完了・待機。同じグループ内では出現順で固定（カードが飛び回らない）
- 表示切替（カードの出現・退場、展開・格納、ミニ表示、設定パネル、トースト）はフェード + 高さの伸縮でアニメーションする。Windows の「アニメーション効果」がオフなら即時切替

ヘッダーのボタン（左から）: ヘッダーのみに格納 / ミニ表示（1行/セッション） / 最前面固定 / 設定 / 閉じる。
ヘッダーをドラッグして移動でき、位置は保存される。上下左右の辺と四隅にカーソルを合わせるとつまみが出て、ドラッグでその辺からサイズを変えられる（反対側の辺は動かない）。幅は既定 360、下限はその 3 分の 1、上限は画面幅。高さは既定では内容に合わせて伸び（画面の 7 割まで）、上下の辺をドラッグするとその高さに固定され、カード一覧が残りを埋める。上下の辺をダブルクリックすると内容に合わせる表示に戻る（ヘッダーのみの表示では常にヘッダーの高さ）。幅と高さは保存される。幅を広げるとカードは入るだけの列（1 列 280 以上）に左上から Z 字状に並び、横長なら横並び、大きくすればタイル状になる。細くすると経過時間を隠し、下限付近では 1 セッション 1 行の表示になる。設定パネルでは不透明度（スライダー、30〜100%）、完了・待機中セッションの非表示、Windows ログイン時の自動起動を切り替えられる。テーマは Windows のアプリテーマ（ライト/ダーク）に追従する。

### ウィンドウの自動整列

下端に常に並ぶアイコン（カーソルを合わせると説明が出る）でセッションのウィンドウを整列する。ボードのあるモニターの、ボードを避けた広い側に並べる。

- 橙が進行中（思考中・ツール実行中）、黄が判断が必要（ヘッダーの「確認待ち」と同じ条件）。それぞれ縦列（全高のウィンドウを横に並べる）とタイル（左上から右へ、次の行へと Z 字状）がある
- 各ウィンドウは、それ以上広げても本文の幅が変わらない幅までにとどめ、左から詰めて並べる（タイルも同じ。右端のスイッチをオンにすると右から詰める。並び順は左から先頭のまま）。デスクトップアプリは会話欄が中央に一定幅で表示されるため、ウィンドウ幅 820（論理ピクセル。125% 表示で約 1025px）を上限とする（2026-11 時点の実測）。CLI のターミナルは幅いっぱいに折り返すため上限なしで、デスクトップアプリが使わなかった幅を受け持つ
- 同じアイコンを続けて押すと順送りする: 先頭（縦列は左端、タイルは左上）が末尾（右端、右下）へ移り、残りが繰り上がる。閉じたウィンドウは抜け、新しいものは末尾に加わる
- 対象はセッション固有のウィンドウのみ。デスクトップアプリのメインウィンドウ内のセッションは、全セッションで共有のウィンドウなので動かさない。別ウィンドウで開くと対象になる

### Claude Code からの確認・通知

Claude Code がユーザーに求めている確認や、出している通知をカード内の帯で表示し、ヘッダーに「N 件確認待ち」と出す。新しい確認が来るとボード内のトーストでも知らせる。確認待ちのセッションは一覧の先頭に並び、「完了・待機中を隠す」でも隠れない。

| 表示 | きっかけ | 消えるタイミング |
|---|---|---|
| 許可の確認（何を許可するか: コマンド・ファイル・URL など） | `PermissionRequest` / `Notification`(permission_prompt) | ツールが実行された（PostToolUse）・ターン終了 |
| 質問への回答待ち（質問文） | `AskUserQuestion` の呼び出し | 回答された（PostToolUse） |
| プランの承認待ち | `ExitPlanMode` の呼び出し | 承認・却下された（PostToolUse） |
| 判断待ち（デスクトップアプリのターン末尾の質問） | transcript の `wrap_up` の `ask` | ユーザーが次のプロンプトを送った |
| 入力待ち・その他の通知（青） | `Notification`(idle_prompt ほか) | 次のプロンプト・ツール実行 |

## 前提

- Windows 10 / 11
- Node.js（hook がこれで動く）
- .NET 10 SDK（ビルド用。実行だけなら .NET 10 Desktop Runtime。exe はフレームワーク依存で約 200KB）
- 他のアプリには依存しない。claude-status-tray-windows などの hooks や常駐アプリは不要。

## インストール

```powershell
git clone https://github.com/haruki-sagara/ClaudeTaskBoard.git
cd ClaudeTaskBoard
dotnet publish .\ClaudeTaskBoard.csproj -c Release -p:PublishSingleFile=true -o .\dist
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

1. `dist\ClaudeTaskBoard.exe` を `%LOCALAPPDATA%\ClaudeTaskBoard\` にコピー
2. `hooks\hook.js` を `~/.claude/taskboard/hook.js` にコピーし、`~/.claude/settings.json` に hook を登録（既存の hook は残す。初回のみ `settings.json.bak-taskboard` にバックアップ。再実行しても重複しない）
3. 自動起動が有効なら、その登録先をインストール先の exe に更新
4. デスクトップにショートカット「Claude Task Board」を作成して起動

アンインストールは `.\uninstall.ps1`（自分の hook だけを外し、アプリ・自動起動・インストール先・ショートカットを削除）。

### フォルダの右クリックメニュー

インストールすると、フォルダの右クリックに「Claude Code で開く」が追加され、デスクトップアプリでそのフォルダの新規 Claude Code セッションを開く（`claude://code/new?folder=…` を `ClaudeTaskBoard.exe --open-folder` が組み立てる。`HKCU` のみ変更。管理者権限は不要）。Windows 11 では「その他のオプションを確認」（Shift+F10）の中に出る。メニューだけ外す場合は次のとおり。

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\context-menu.ps1 -Uninstall
```

## 安定性・トラブル時の調べ方

- **終了は確認つき**: ✕ と Alt+F4 はボード内で確認してから終了する（Windows のログオフ・シャットダウン時は確認しない）。
- **落ちても戻る**: ボードは 5 秒ごとに `~/.claude/taskboard/board.json` に生存の印を書く。印が 30 秒以上途絶えていれば、次のセッション開始・プロンプト送信時に hook がボードを起動し直す（explorer 経由なので、Claude デスクトップアプリの中ではなく通常のアプリとして起動する）。ユーザーが自分で終了した場合は起動しない（次に手動で起動すると解除）。
- **画面外対策**: スリープ復帰や画面構成の変更で画面外に出たら、画面内に戻す。
- **例外**: UI スレッドの例外は記録して動作を続ける（1 分に 20 回を超えたら終了し、上記の仕組みで再起動）。
- **ログ**: `~/.claude/taskboard/logs/board.log`（1MB で `board.log.1` にローテート）。起動・終了とその理由・例外・スリープ／復帰を記録する。`start` の後に `exit` が無ければ、強制終了またはネイティブクラッシュ（その場合は Windows のイベントビューアー「アプリケーション」も確認）。

## データの取り方

| 情報 | 取得元 |
|---|---|
| セッション・状態・経過時間・確認/通知 | 自前の hook（`hooks\hook.js`）が書く `~/.claude/taskboard/state.d\<session_id>.json`。生存判定は ts が 4 時間以内。24 時間更新のないファイルはボードが削除する（一括削除はしない） |
| タイトル | transcript の `custom-title` 行 |
| タスク一覧 | transcript の `mcp__ccd_turn__update_plan`（全体 / advance / done / start）、`mcp__ccd_turn__wrap_up`（全完了）、CLI の `TodoWrite` |
| 判断待ち | transcript の `mcp__ccd_turn__wrap_up` の `ask`（その後にユーザーのプロンプトが来たら解除） |
| 今週の使用量 | Claude Code と同じ `api.anthropic.com/api/oauth/usage`（非公開 API のため変更の可能性あり）の `seven_day`。5 分ごとに取得 |

今週の使用量について: 公式の上限はトークン数で公開されていないため、使用率（%）で表示する。読めない・取得失敗のときは古い値を残さず「取得不可」と表示する。

- ログイン情報は `~/.claude/.credentials.json`（Claude Code の CLI のもの）をリクエストごとに読む。アプリ独自の保存先は持たず、ログにも出さない
- デスクトップアプリはこのファイルを更新しないため、アクセストークンの期限が 5 分以内に迫ると、常駐のボードが CLI と同じ方式で更新して書き戻す（`platform.claude.com`。他の項目は保持）。非公開の手順で、CLI と同時に更新すると CLI の再ログインが必要になる場合がある
- トークンの送信先は `api.anthropic.com` と `platform.claude.com` のみ。スナップショット等の起動では更新しない
- リフレッシュトークンが無効なら「CLI の再ログインが必要です」と表示し、再送しない。カード内の「CLIで再ログインコマンドを送信」でターミナルに `claude auth login` を起動できる（ログイン情報の不足・期限切れ・無効のときだけ出る）。ログインし直すと、数秒で自動的に取得を再開する（待機中の確認はローカルファイルの読み取りのみで、通信しない）

環境変数 `TASKBOARD_HOME` を設定すると、hook とボードの両方がその場所を使う（実データに触れずに試すため）。

transcript は前回読んだ位置からの**追記分だけ**を読み、関係する行だけを JSON 解析する（数十 MB の transcript でも初回以外は軽い）。
サブエージェント（`isSidechain: true`）のタスク操作は無視する。

未対応: CLI の新しいタスクツール（`TaskCreate` / `TaskUpdate`）。手元の transcript に実例がなく、入力形式を確認できていないため。

## ビルド・実行

```powershell
dotnet publish .\ClaudeTaskBoard.csproj -c Release -p:PublishSingleFile=true -o .\dist
.\dist\ClaudeTaskBoard.exe
```

二重起動すると既存のウィンドウが前面に出る。

- `ClaudeTaskBoard.exe --selftest` — データ層（タスク再構成・transcript 追記読み）の自己テスト。終了コード 0 で成功。
- `ClaudeTaskBoard.exe --snapshot out.png [--expanded] [--settings] [--dark|--light] [--usage-percent N] [--width N] [--height N] [--header-only]` — 実データで画面外に描画して PNG 保存（見た目の確認用。`--usage-percent` は使用量をデモ値に固定）。

設定は `%USERPROFILE%\.claude\taskboard\settings.json`。

## ライセンス・謝辞

[MIT](LICENSE)。

hook の設計（セッションごとの状態ファイル、hook イベントと状態の対応、アトミックな書き込み）は
[claude-status-tray-windows](https://github.com/vyshnav-suresh/claude-status-tray-windows)（MIT。元は
[m1ckc3s/claude-status-bar](https://github.com/m1ckc3s/claude-status-bar)）を参考にしています。ライセンス表記は [LICENSE](LICENSE) に同梱。
