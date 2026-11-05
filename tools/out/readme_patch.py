import io

p = r"D:\Beautiful\README.md"
s = io.open(p, encoding="utf-8").read()


def rep(a, b):
    global s
    if a not in s:
        print("MISS:", a[:50])
        return
    s = s.replace(a, b, 1)


rep('想知道 v3 改了什么 → 看「v3 变更（对着反馈的七条）」。',
    '想知道最近一轮改了什么 → 看「v4.3 变更」一节。')

rep('`dotnet test tests/Beautiful.Core.Tests -c Release` **1274 通过**',
    '`dotnet test tests/Beautiful.Core.Tests -c Release` **1345 通过**')

rep('`dotnet test` **1274 通过**',
    '`dotnet test` **1345 通过**')

rep('对照实验（同一首歌、唯一变量是时长，`tools\\lyric-probe`）：时长 0 → `NetEase: 未命中`，退化到 LRCLIB（2.1s）；时长 182s → `NetEase 命中 36 行`（0.3s）。',
    '对照实验（同一首歌、唯一变量是时长，`tools\\lyric-probe`）**修之前**：时长 0 → `NetEase: 未命中`，退化到 LRCLIB（2.1s）；时长 182s → `NetEase 命中 36 行`（0.3s）。'
    '**修之后**冷缓存、三首都**不传时长**：溯 → `NetEase 命中 33 行` 0.5s；We Don\'t Talk Anymore → `NetEase 命中 83 行（含译文）` 0.5s；'
    '晴天 → NetEase **明确拒绝**（`搜索到 8 条候选，标题对得上但艺术家都对不上（可能是同名翻唱）`）→ LRCLIB 兜住 42 行 5.4s。'
    '顺带修掉 LRCLIB 的三处自伤：带 `duration=0` 去问 `/api/get` 必返 400（改为跳过）、时长未知时「取时长最接近」退化成「取最短的那首」（等于随机贴别人的歌词，改为取相关度第一条带同步歌词的）、'
    '单源超时 3s 卡在 LRCLIB 搜索实测 2.5~2.8s 的边界上把自己掐成 `TaskCanceledException`（放到 6s）。')

rep('**放大 / 倒影 / 媒体胶囊 / 壁纸取色 / 任务栏玻璃壳五个开关砖块**',
    '**放大 / 倒影 / 顶栏媒体药丸 / 壁纸取色 / 任务栏玻璃壳五个开关砖块**')

io.open(p, 'w', encoding='utf-8').write(s)
print('done')
