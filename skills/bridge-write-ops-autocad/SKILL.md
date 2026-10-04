---
name: bridge-write-ops-autocad
description: Working command patterns for WRITE operations in AutoCAD via nano-bridge — create new dwg, insert blocks (regular + dynamic), set dynamic block Lookup parameters, fill attribute references, insert external dwg as block. Use this skill whenever you need to MODIFY an AutoCAD drawing through bridge (not just read/snapshot). Covers 6 bridge gotchas. Especially critical for any task involving cyrillic strings (block names, attribute values, file names) — bridge double-encoding will silently mojibake unless you use the file-based LSP workaround.
---

# bridge-write-ops-autocad — рабочие команды для модификации AutoCAD через bridge

> `<TEMP>` в путях ниже — это `%TEMP%` хоста, записанный через прямые слэши.

## Why this skill exists

Write-операции через bridge упираются в несколько неочевидных особенностей ActiveX/LISP. Этот skill — **набор проверенных рабочих паттернов** (проверено на создании демо-чертежа с рамкой, штампом и узлом). Применяй ВСЕГДА, когда задача требует модифицировать чертёж (не только снимать snapshot).

## ⚠️ 6 bridge gotchas (порядок важности)

### Gotcha #1: ActiveX `_OPEN` НЕ переключает MdiActiveDocument
**Симптом:** после `(command "_OPEN" path)` bridge продолжает читать старый документ.
**Решение:** `vla-Open` + `vla-put-ActiveDocument`.

### Gotcha #2: `vla-put-ActiveDocument` без `vla-Activate` — bridge снимает visible окно (не active-via-API)
**Симптом:** `get_document_summary` показывает doc A, `get_view` снимает doc B.
**Решение:** добавить `(vla-Activate new-doc)` после `vla-put-ActiveDocument`.

### Gotcha #3: Cyrillic LISP literals в bridge → mojibake в AutoCAD
**Симптом:** `(vla-put-TextString att "ДЕМО-001")` через `run_lisp` → mojibake `РРЎРџ...` в drawing.
**Причина:** bridge encodes LISP HTTP body → cp1251 → AutoCAD re-interprets → double encoding.
**Решение:** генерировать LSP файл через Python с **явным cp1251** encoding, потом `(load "path/script.lsp")`. AutoCAD читает файл с диска напрямую, double-encoding нет. Pattern:
```python
LSP = '(vla-put-TextString att "ДЕМО-001")'
Path("script.lsp").write_bytes(LSP.encode('cp1251'))
```
Затем bridge: `(load "C:/.../script.lsp")` — простой ASCII path.

### Gotcha #4: Cyrillic в `wcmatch` / `vl-string-search` filter — не сработает
**Симптом:** `(wcmatch nm "СО_Рамка*")` через bridge → mojibake pattern → 0 matches.
**Решение:** искать блоки **не по имени** а по уникальному свойству — например attribute_def count, или layer name (latin), или position. Пример: найти `СО_Рамка_штамп` через `attribute_def_count == 10`.

### Gotcha #5: Dynamic block Lookup parameter требует `vlax-vbInteger`, не string/Long
**Симптом:** `(vla-put-Value lookup-prop (vlax-make-variant "3" vlax-vbString))` → «Неверный ввод». Также vbLong fails.
**Решение:** type code 2 = `vlax-vbInteger`. Только это работает для Lookup tables:
```lisp
(vla-put-Value fmt-prop (vlax-make-variant 3 vlax-vbInteger))
```
Значения — integer 1..N где N = кол-во вариантов в Lookup table. После set обязательно `(vla-Update block-ref)`.

### Gotcha #6: Constant attributes НЕ в `vla-GetAttributes` BlockReference
**Симптом:** vla-GetAttributes возвращает 8 AttributeReferences хотя block definition имеет 10 attribute_defs. 2 пропущенных — constant flag.
**Причина:** constant attributes хранятся в block definition (как обычная геометрия), не создают AttributeReference на каждом инстансе.
**Решение:** для constant нужен `_BEDIT` block definition (нежелательно — сломает другие применки). Workaround — оставить как «known limitation» в notes, engineer ручную замену сделает.

## Working command patterns

### Pattern 1: Создать новый dwg через ActiveX

```lisp
(vl-load-com)
(setq acad (vlax-get-acad-object))
(setq new-doc (vla-Add (vla-get-Documents acad) ""))   ; "" = default template
(vla-SaveAs new-doc "<TEMP>/<workspace>/<file>.dwg")
(vla-put-ActiveDocument acad new-doc)
(vla-Activate new-doc)   ; КРИТИЧНО — Gotcha #2
```

После этого `get_document_summary` покажет правильный doc, `get_view` снимет его же.

### Pattern 2: Копировать block definition между документами

```lisp
; Source: palette-doc (где определение есть)
; Destination: new-doc (куда копируем)
(setq source-blk (vla-Item (vla-get-Blocks palette-doc) "BlockName"))   ; or find by attrs count

; CRITICAL: safearray of vlax-vbObject (Dispatch refs), NOT Long IDs
(setq objs (vlax-make-safearray vlax-vbObject '(0 . 0)))
(vlax-safearray-put-element objs 0 source-blk)
(setq objs-variant (vlax-make-variant objs))

; Owner is the Blocks collection of destination doc
(vla-CopyObjects palette-doc objs-variant (vla-get-Blocks new-doc))
```

После этого block definition появляется в Blocks collection нового документа.

### Pattern 3: Вставить block reference

```lisp
(setq ms (vla-get-ModelSpace new-doc))
(setq insert-pt (vlax-3d-point 0 0 0))   ; world coords
(setq block-ref (vla-InsertBlock ms insert-pt "BlockName" 1.0 1.0 1.0 0.0))
                                                 ;  scale-x  y  z  rotation(rad)
```

### Pattern 4: Изменить Lookup parameter dynamic block

```lisp
; First find the right property — usually FIRST in iteration
(setq fmt-prop nil)
(foreach prop (vlax-safearray->list (vlax-variant-value (vla-GetDynamicBlockProperties block-ref)))
  (if (not fmt-prop) (setq fmt-prop prop)))   ; take first; last is usually 'Origin'

; CRITICAL: vlax-vbInteger (type 2), not vbString or vbLong — Gotcha #5
(vla-put-Value fmt-prop (vlax-make-variant 3 vlax-vbInteger))
(vla-Update block-ref)
```

Чтобы узнать что value `3` даёт — циклить 1..N и логировать bbox:
```lisp
(foreach n '(1 2 3 4 5 6 7 8)
  (vla-put-Value fmt-prop (vlax-make-variant n vlax-vbInteger))
  (vla-Update block-ref)
  (vla-GetBoundingBox block-ref 'bbmin 'bbmax)
  ; log bbox …
)
```

### Pattern 5: Заполнить attributes BlockReference

**Для не-cyrillic значений** — inline LISP через `run_lisp`:
```lisp
(setq attrs (vlax-safearray->list (vlax-variant-value (vla-GetAttributes block-ref))))
(foreach att attrs
  (setq tag (vla-get-TagString att))
  (cond
    ((= tag "DATE") (vla-put-TextString att "2026-05-22"))
    ((= tag "SHEET") (vla-put-TextString att "1"))))
```

**Для cyrillic значений** — Python генерирует cp1251 LSP файл, bridge `(load …)`:
```python
LSP = '''(setq attrs (vlax-safearray->list (vlax-variant-value (vla-GetAttributes block-ref))))
(foreach att attrs
  (setq tag (vla-get-TagString att))
  (cond
    ((= tag "ШИФР") (vla-put-TextString att "ДЕМО-001"))
    ((= tag "СТАДИЯ") (vla-put-TextString att "Р"))))'''
Path("C:/.../fill.lsp").write_bytes(LSP.encode('cp1251'))
```
Bridge:
```
mcp__nano-bridge__run_lisp:
  (load "C:/.../fill.lsp")
```

### Pattern 6: Insert external dwg as block

`_-INSERT` command (минус-префикс = command-line, не диалог):
```lisp
(command "_-INSERT"
         "<TEMP>/dwg_phase2_src/dwg_001.dwg"
         "40,74"       ; insertion point as "x,y" string
         "0.01" "0.01" ; scale X, Y
         "0")          ; rotation degrees
```

Полученный block name = stem of file ("dwg_001"). Потом можно работать с ним как с обычным блоком через ActiveX.

**Scaling cheat-sheet** для типовых dwg (реальные mm):
- dwg ~21000×15000 mm → scale **0.01** = 210×150 mm (вписывается в А3 центр)
- dwg ~5000×3000 mm → scale **0.05** = 250×150 mm
- dwg ~1000×600 mm → scale **0.25** = 250×150 mm

### Pattern 7: Bridge не возвращает LISP `(princ)` stdout

**Симптом:** `(princ "result")` в LISP → bridge response не содержит "result".
**Решение:** писать в log файл, потом читать через bash + iconv:
```lisp
(setq logf (open "<TEMP>/claude_bridge/log.txt" "w"))
(write-line "..." logf)
(close logf)
```
```bash
iconv -f cp1251 -t utf-8 "<TEMP>/claude_bridge/log.txt"
```
Log файл в cp1251 (LISP `open` default), iconv в UTF-8 для нормального чтения.

### Pattern 8: Cyrillic в file paths bridge `vla-Open`

Bridge плохо переваривает cyrillic в path. Решение — **ASCII copy в %TEMP%**:
```bash
mkdir -p "<TEMP>/<workspace>/"
cp "C:/Проекты/Демо/<file>.dwg" "<TEMP>/<workspace>/<ascii>.dwg"
```
Потом открывай ASCII copy.

## Full canonical workflow: создать чертёж рамка+штамп+узел

Применяй как шаблон.

### Шаг 1: prep
```bash
mkdir -p "<TEMP>/<workspace_name>/"
```

### Шаг 2: открыть source палитру (если ещё не открыта) — Pattern 1 + bridge gotcha #1,#2

### Шаг 3: create new dwg — Pattern 1

### Шаг 4: copy block definition из палитры в новый dwg — Pattern 2
Найти block по уникальному свойству (Gotcha #4) — например `attribute_def_count`.

### Шаг 5: insert block reference в (0,0) — Pattern 3

### Шаг 6: если dynamic — set Lookup format — Pattern 4

### Шаг 7: fill attributes — Pattern 5 (через Python+cp1251 LSP если cyrillic)
**Constant attributes пропустятся** — Gotcha #6. Зафиксируй в needs_engineer_review.

### Шаг 8: insert external dwg в центр свободной зоны — Pattern 6
Считай координаты центра: для А3 (420×297), штамп ~ (235..420, 0..55) → центр свободной (175, 145).

### Шаг 9: vla-Save + snapshot для verify

### Шаг 10: чек-лист
- [ ] `document.name` правильный (Gotcha #2 не воспроизвёлся)
- [ ] Snapshot показывает рамку с правильным форматом
- [ ] Attributes без mojibake (если cyrillic — через cp1251 LSP)
- [ ] Узел в центре, не пересекает штамп
- [ ] Файл сохранён на диск

## Bridge tools reference (для write-ops)

- `mcp__nano-bridge__run_lisp` (confirm=true) — ВСЁ через него, либо inline LISP, либо `(load "file.lsp")`
- `mcp__nano-bridge__get_document_summary` — verify active doc после open
- `mcp__nano-bridge__execute_command` (confirm=true) — для простых command-line операций (`_ZOOM E`, `_SAVE`), хотя проще через `run_lisp` + `(command ...)`
- `mcp__nano-bridge__get_view` — visual verify результата
- `mcp__nano-bridge__camera_zoom_extents/window` — для view setup перед snapshot

## Когда этот skill НЕ применять

- Чистый read/snapshot dwg без модификации — используй [[inspect-multilayout-dwg]]
- Modify в рабочем .dwg пользователя — сначала dry-run и подтверждение (см. [[safe-write-workflow]])
- Создание новых block definitions (требует BEDIT) — нежелательно, оставь engineer'у
