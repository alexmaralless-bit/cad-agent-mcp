---
name: inspect-multilayout-dwg
description: Correct workflow for inspecting AutoCAD/NanoCAD dwg files via the nano-bridge — especially typical/template dwg where multiple "листы" are laid out in Model space side-by-side, AND when you switch documents while a project drawing is already open. Use this for ANY dwg snapshot work — even single-layout — because two failure modes (unreadable multi-layout extents + bridge snapshotting the wrong document) both manifest only after you trust the snapshot. The fix has three parts: open via ActiveX AND activate, grid-overlay to read layout bboxes, then per-layout zoom_window.
---

# inspect-multilayout-dwg — обязательный порядок при снапшоте dwg через bridge

> `<TEMP>` в путях ниже — это `%TEMP%` хоста, записанный через прямые слэши.

## Why this skill exists

Bridge `get_view` использует Win32 `PrintWindow` на физическом окне AutoCAD. Это даёт **два независимых failure mode**, оба из которых проявляются только когда ты УЖЕ доверился снимку:

**Failure mode #1 — unreadable text для multi-layout dwg.**
Окно AutoCAD ~2548×1340 px. Если в Model space лежит 2–4–8 листов side-by-side (типовки часто такие), при `zoom_extents` каждый лист становится ~600×400 px → текст нечитаем → subagent на этом PNG строит мусорный template.

**Failure mode #2 — bridge снимает ВИДИМОЕ окно, не active-через-API.**
Если у тебя в AutoCAD открыт project drawing (например, `ДЕМО-001.dwg`) **и** ты дополнительно открыл palette через `vla-Open` + `vla-put-ActiveDocument` — ActiveX API переключает logical activeDocument, но физическая видимость окна остаётся на проекте. `get_document_summary` (читает API) покажет палитру; `get_view` (снимает window) покажет проект. Получаешь **противоречивую информацию** в одной сессии и долго не понимаешь почему.

Порядок: сначала zoom extents, чтобы понять, сколько листов в модели, затем сетка координат, затем прицельный zoom по каждому листу.

Это правило, не рекомендация. Применять для **любого** dwg-снапшота — даже если ожидаешь один лист и один открытый документ. Стоимость лишнего шага низкая, цена ошибки высокая (subagent делает мусор, или ты целый разговор обсуждаешь не тот документ).

## Обязательный workflow

### Шаг 0. Подготовка пути (если cyrillic)

Bridge плохо переваривает cyrillic в file path при `vla-Open`. Если исходный dwg в папке `C:/Проекты/Демо/...` — скопируй в ASCII-temp:

```bash
mkdir -p "<TEMP>/<workspace-name>/"
cp "<cyrillic-source.dwg>" "<TEMP>/<workspace-name>/<ascii_name>.dwg"
```

### Шаг 1. ОТКРЫТЬ + АКТИВИРОВАТЬ через ActiveX

**КРИТИЧНО:** `(command "_OPEN" ...)` LISP — НЕ подходит. AutoCAD кеширует `MdiActiveDocument`, после `_OPEN` следующие bridge вызовы будут идти по СТАРОМУ документу.

**Правильный шаблон** — три ActiveX вызова в порядке:

```lisp
(vl-load-com)
(setq acad (vlax-get-acad-object))
(setq new-doc (vla-Open (vla-get-Documents acad) "<TEMP>/<workspace>/<file>.dwg"))
(vla-put-ActiveDocument acad new-doc)
(vla-Activate new-doc)              ; ← КРИТИЧНО если в AutoCAD открыто >1 окна
(princ "OK")
```

**Зачем `vla-Activate` отдельно от `vla-put-ActiveDocument`?**
- `vla-put-ActiveDocument` — переключает **logical activeDocument** в API. `get_document_summary` после этого покажет правильный doc.
- `vla-Activate` — **bring window to front** (Win32 focus). `get_view` после этого снимет правильный doc.

Если открыто **только один** документ (например, ты сделал `_NEW` + `vla-Open` дополнительно одного, всего 2 окна) — `vla-Activate` обязательно. Если в AutoCAD открыт только что-открытый dwg (один MDI child) — `vla-Activate` безвреден (no-op).

### Шаг 2. ВЕРИФИЦИРОВАТЬ что bridge видит правильный doc

Перед тем как делать «настоящий» workflow, сделай **тестовый snapshot** и проверь что bridge видит ожидаемый документ:

```
mcp__nano-bridge__get_document_summary
```

Сверь поле `document.name` — это должен быть путь к ASCII-temp dwg который ты только что открыл. Если показано что-то другое (project drawing, какой-нибудь .sv$ autosave) — повтори Шаг 1 целиком (`vla-Activate` мог не сработать асинхронно).

Дополнительно: bridge timeout на первом `get_document_summary` после `vla-Open` — частое явление. Если 35s timeout — `mcp__nano-bridge__healthz` + retry summary. Если упорно timeout: `bash rm "<TEMP>/claude_bridge/snapshot.json"` + retry.

### Шаг 3. Zoom extents + сетка + первый snapshot

```
mcp__nano-bridge__camera_zoom_extents
mcp__nano-bridge__overlay_grid       # без аргументов = auto step
mcp__nano-bridge__get_view
```

**Сверь снимок с ожиданием:** видишь содержимое того dwg который открыл? Сетка покрывает разумную область (не 70 000 × 90 000 единиц вместо ожидаемых 200 × 200 — это симптом что попал на проект, а не палитру)?

Если снимок показывает **другой** документ — это **Failure mode #2 manifest**. Вернись к Шагу 1 и добавь `vla-Activate` если забыл, или повтори (асинхронная активация иногда задержалась).

### Шаг 4. Чтение bbox каждого листа из снимка с сеткой

PNG с сеткой даёт тебе **координатную сетку поверх содержимого**. Для каждого визуально различимого листа запиши его bbox в drawing-units:

```
Лист 1 "План трассы":     X[0..50],   Y[0..30]
Лист 2 "Узел колодца":    X[60..90],  Y[0..30]
Лист 3 "Спецификация":    X[0..50],   Y[40..70]
```

Если лист **один** (что подтверждено визуально, не по предположению) — переход к Шагу 6 (целиковый extents-снимок уже валиден).

### Шаг 5. Per-layout zoom + снимок для КАЖДОГО листа

```
mcp__nano-bridge__camera_zoom_window  bbox=(xmin, ymin, xmax, ymax)
mcp__nano-bridge__get_view
```

Сохраняй PNG копированием **сразу после каждого get_view** — следующий вызов overwrite'нет:
```bash
cp "<TEMP>/claude_bridge/snapshot.png" \
   "<TEMP>/<workspace>/<id>/layout_<NN>_<short_name>.png"
```

Overlay сетка автоматически смоется в `get_view`. Не вызывать `overlay_clear` отдельно.

### Шаг 6. Финальный extents-снимок для контекста

После всех per-layout снимков (или вместо них для single-layout dwg):

```
mcp__nano-bridge__camera_zoom_extents
mcp__nano-bridge__get_view
```

Этот PNG нужен subagent'у для общей навигации — «где какой лист в исходной модели». Сохрани как `<id>/extents_grid.png` или `<id>/extents_context.png`.

## Чек-лист перед тем как двинуться дальше

- [ ] dwg открыт через `vla-Open` + `vla-put-ActiveDocument` + `vla-Activate` (все три!)
- [ ] `get_document_summary` подтвердил что bridge видит **правильный** документ
- [ ] первый snapshot с сеткой визуально соответствует ожидаемому содержимому (не показывает проект вместо палитры)
- [ ] zoom_extents + grid сделан, я знаю **сколько листов** в Model space
- [ ] координаты bbox каждого листа записаны (хотя бы в текстовом ответе)
- [ ] для каждого листа отдельный per-layout снимок сделан
- [ ] финальный extents-снимок для контекста есть

Если хотя бы один пункт пропущен — **не отдавать материал subagent'у на template generation**. Он построит мусор, или ещё хуже — построит мусор по совсем другому документу.

## Anti-patterns (не делать)

1. **`zoom_extents` → сразу `get_view`** для multi-layout dwg → нечитаемый текст.
2. **Угадывать координаты листов** без grid overlay → ошибочный zoom_window.
3. **Передавать subagent'у только extents-PNG**, надеясь что «он разберётся» — он не разберётся, текст нечитаем.
4. **`(command "_OPEN" ...)`** для смены документа — AutoCAD кеширует MdiActiveDocument, bridge продолжает работать со старым doc.
5. **`vla-put-ActiveDocument` без `vla-Activate`** когда открыто >1 MDI окно — `summary` покажет правильный doc, `get_view` снимет неправильный.
6. **Доверять `document.name` в run_lisp response** — оно показывает doc до выполнения команды, не после. Проверять через свежий `get_document_summary`.
7. **Доверять одному только `get_document_summary`** — он читает API, не window. Проверять снимком (видно ли ожидаемое содержимое).

## Bridge tools reference

Из `mcp__nano-bridge`:
- `healthz` — sanity check, нет ли upstream проблем
- `get_document_summary` — extents/layers/blocks из API (читает activeDocument)
- `camera_zoom_extents` — fit модели в окно
- `camera_zoom_window` — bbox `(min:{x,y}, max:{x,y})` в drawing units
- `overlay_grid` — координатная сетка (auto step, или `step=N`). Auto-cleared после `get_view`.
- `overlay_clear` — нужен ТОЛЬКО если ты сделал overlay но НЕ снял после (редкий случай)
- `get_view` — PNG window-capture + JSON metadata (cleared_count, capture method)
- `query_objects` — entity-level extraction (BlockReference + InsertPoint + attributes — для семантики)
- `run_lisp` — единственный путь к ActiveX (`vla-Open` + `vla-put-ActiveDocument` + `vla-Activate`)

## Когда этот скилл НЕ применять

- Снапшот пустой / однообъектной модели (просто `healthz` или `probe`)
- AutoCAD только что запущен с одним документом (этот документ уже active visually, ничего не открываешь дополнительно)
- Пользователь явно говорит «не нужно per-layout, мне общий вид»

Во всех остальных случаях — **применяй этот workflow целиком**. Цена лишних 2–3 bridge calls (~10s) много меньше цены ошибки (целая сессия обсуждения не того документа, мусорный template от subagent).
