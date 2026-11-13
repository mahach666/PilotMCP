# Замечания

Замечания (аннотации) создаются к документам XPS и PDF. Страницы нумеруются с 0.

## Формат: Markup и legacy

| Формат | Для чего | Как выбрать |
| ------ | -------- | ----------- |
| Markup (по умолчанию) | Клиент Pilot 26.20+; в объекте замечания пишутся атрибуты `markups` и `markups_version` | `format=markup` или параметр не указан |
| legacy | Серверы и клиенты старше 26.20; атрибут `annotation` | `format=legacy` (`AnnotateText`, `CreateAnnotation`) |

В legacy-формате новые фигуры и параметры недоступны: только `rectangle`, `ellipse`, `triangle`, `arrow`, `point`, без `strokeWidth`, `points`, `cloud`, `startEnding`, `endEnding`, а `style` в `AnnotateText` только `highlight`. Запрещённая комбинация возвращается ошибкой.

`CreateMarkupAnnotation` работает только с Markup.

## Инструменты

| Инструмент | Что делает |
| ---------- | ---------- |
| [`AnnotateText`](tools/annotations.md#annotatetext) | Находит слово или фразу в тексте документа и выделяет её |
| [`CreateAnnotation`](tools/annotations.md#createannotation) | Одна графическая фигура |
| [`CreateMarkupAnnotation`](tools/annotations.md#createmarkupannotation) | Одно замечание из нескольких фигур (Markup) |
| [`GetAnnotations`](tools/annotations.md#getannotations) | Читает замечания документа |
| [`BuildAnnotationXml`](tools/annotations.md#buildannotationxml) | XML legacy-разметки без создания объекта |

## Координаты

Координаты и размеры фигур передаются в **пикселях изображения**, полученного от `GetDocumentPageImage`, с началом в левом верхнем углу. Параметр `dpi` у инструментов замечаний должен совпадать с фактическим `dpi` изображения (по умолчанию 150). Размеры страниц берутся из того же файла документа (XPS, иначе PDF).

`GetDocumentPageImage` принимает `dpi` от 36 до 300; если длинная сторона получается больше 2000 px, `dpi` снижается автоматически. Фактический `dpi` указан в ответе, его нужно передавать в `CreateAnnotation`, `CreateMarkupAnnotation` и `GetAnnotations`.

Рекомендуемая последовательность:

1. `GetDocumentPageImage(documentId, page, dpi)` и запомнить фактический `dpi`.
2. По изображению определить пиксельные координаты.
3. `CreateAnnotation` или `CreateMarkupAnnotation` с теми же `page` и `dpi`.
4. `GetAnnotations(documentId, dpi)` для проверки.

## Фигуры и параметры

Допустимые значения `shape`: `rectangle`, `ellipse`, `triangle`, `arrow`, `line`, `polyline`, `polygon`, `ink`, `point`.

| Фигура | Смысл `x`, `y`, `width`, `height` |
| ------ | --------------------------------- |
| `rectangle` | `x`, `y` — левый верхний угол; `width`, `height` — размеры |
| `ellipse` | `x`, `y` — центр; `width`, `height` — полные диаметры |
| `triangle` | в рамке `x`, `y`, `width`, `height`: основание по нижней стороне, вершина посередине сверху |
| `arrow`, `line` | `x`, `y` — острие (конец линии); хвост в точке `x + width`, `y + height` |
| `polyline`, `polygon`, `ink` | вершины в `points`: `x1,y1;x2,y2;...` (минимум 2, 3 и 2 вершины соответственно) |
| `point` | `x`, `y` — левый верхний угол стикера размером 24x24 |

Дополнительные параметры Markup:

- `color`: `red`, `green`, `blue`, `yellow`, `pink`, `black` или `#RRGGBB`;
- `strokeWidth`: толщина линии в DIP, больше 0 (по умолчанию 2);
- `cloud`: облачная граница, только для `polyline` и `polygon`;
- `startEnding`, `endEnding`: окончания линии `none` или `openarrow`, только для `arrow` и `line`; у `arrow` по умолчанию `openarrow` на острие.

Для `rectangle`, `ellipse` и `triangle` `width` и `height` должны быть больше 0. Стиль `AnnotateText` в Markup: `highlight` (по умолчанию), `underline`, `strikeout`.

Если `content` у `CreateAnnotation` похож на исправление ошибки в тексте (есть слова «ошибк», «опечатк», «должно быть» или «орфограф» и слово в кавычках либо пара «слово -> исправление»), вызов перенаправляется в `AnnotateText` по найденному слову. Графическую фигуру в этом случае не ждите: используйте для неё `content` без таких формулировок.

### Пример `shapesJson`

Одно замечание из прямоугольника и стрелки:

```json
[
  {"shape": "rectangle", "x": 150, "y": 150, "width": 200, "height": 100, "color": "blue"},
  {"shape": "arrow", "x": 150, "y": 150, "width": 80, "height": 60}
]
```

Поля такие же, как у `CreateAnnotation`: `shape`, `x`, `y`, `width`, `height`, `page`, `color`, `strokeWidth`, `points`, `cloud`, `startEnding`, `endEnding`. Неизвестные поля и `null` вместо фигуры дают ошибку; массив должен быть непустым.

## Чтение: GetAnnotations

`GetAnnotations(documentId, dpi)` возвращает JSON-массив замечаний: ID, тип, текст, автор, дата создания, версия файла, формат (`markup` или `legacy`) и фигуры (вид, страница, ограничивающий прямоугольник `x`, `y`, `width`, `height` в пикселях при заданном `dpi`, стиль). Замечания из корзины пропускаются; нечитаемое замечание выводится с полем `error`. Для Markup читается версия, совпадающая с текущей версией файла, иначе последняя.

## Чего нет и почему

Эти возможности намеренно не предлагаются, потому что клиент Pilot 26.20 их не поддерживает:

- **Текст на странице (freetext).** Не создаётся: на этом виде замечаний падает клиент 26.20. Для текстовых пометок используйте `AnnotateText` (замечание к тексту) и `point` (стикер).
- **Волнистая линия (squiggly).** Список замечаний клиента 26.20 падает на таком замечании с ошибкой `Unknown MarkupSubtype: Squiggly`. Интерфейс клиента сам создаёт только выделение, подчёркивание и зачёркивание.
- **Окончания линий, кроме `none` и `openarrow`.** Клиент отрисовывает только эти два.
- **Заливка и стиль линии.** Клиент их не рисует, поэтому фон у фигур не задаётся, а линия всегда сплошная.
