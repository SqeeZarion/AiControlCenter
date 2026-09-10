# Directions у ControlPlane

ControlPlane є єдиним власником каталогу напрямків. `Direction` — конфігураційний контейнер для майбутніх можливостей платформи; він не запускає Agents, Workflows або зовнішні інтеграції.

## Модель та invariants

| Поле | Правило |
| --- | --- |
| `Id` | непорожній UUID |
| `Name` | Unicode `White_Space` на краях видаляється, внутрішні послідовності стискаються до ASCII-пробілу; 2–100 Unicode scalar values |
| `Code` | lowercase slug; whitespace/`_` перетворюються на `-`; 2–64 ASCII літери/цифри |
| `Description` | optional, trim, до 1000 символів |
| `Icon` | optional, trim, до 100 символів |
| `Status` | `Active` або `Inactive` |
| `SortOrder` | від 0 до 100000 |
| timestamps | UTC `CreatedAt`, `UpdatedAt`, optional `ArchivedAt` |
| `Version` | PostgreSQL `xmin`, передається у mutation DTO |

`Code` унікальний для всіх записів, включно з архівованими. Архівований Direction не можна редагувати, змінювати його status або sort order: його потрібно спочатку відновити. Archive/restore не виконують hard delete. Повторна операція над уже досягнутим станом не створює нового запису, якщо клієнт передав поточну version.

Список за замовчуванням виключає архів. Він підтримує `includeArchived`, optional `status` і case-insensitive search за назвою. Порядок завжди стабільний: `SortOrder`, `Name`, `Id`. `Name` однаково рахується у Domain, Application, Angular і PostgreSQL як Unicode scalar values: один non-BMP символ на кшталт emoji — це одна одиниця, а base character і combining mark — дві. Нормалізація пробілів використовує Unicode-властивість `White_Space`, тому охоплює, зокрема, `U+0085`, `U+00A0`, `U+2007` і `U+202F`. `U+FEFF` не належить до `White_Space` і не видаляється. NFC/NFKC canonical normalization навмисно не виконується: візуально схожі canonical sequences можуть залишатися різними значеннями.

Список має обов'язкову offset pagination: `page` починається з 1, `pageSize` за замовчуванням дорівнює 20, допустимий діапазон — 1–100. Фільтри застосовуються до pagination. Відповідь містить `items`, `page`, `pageSize`, `totalCount` і `totalPages`; некоректні значення повертають 400. Стабільне сортування робить повторний запит до незмінного snapshot детермінованим, але offset pagination не створює snapshot між окремими HTTP-запитами: конкурентне додавання, архівування, відновлення або зміна sort order може зсунути межі сторінок і спричинити повтор чи пропуск елемента. Після власної mutation клієнт повинен перезавантажити список, а не вважати вже завантажені сторінки snapshot-ом.

## REST API

Зовнішній base URL — `/api/control-plane/v1/directions`; внутрішній ControlPlane URL — `/v1/directions`.

| Method і path | Результат | Доступ |
| --- | --- | --- |
| `GET /` | paged список, фільтри та metadata | `AnyPlatformUser` + `PasswordChanged` |
| `GET /{id}` | один Direction або 404 | `AnyPlatformUser` + `PasswordChanged` |
| `POST /` | 201 і створений Direction | `AdminOnly` + `PasswordChanged` |
| `PUT /{id}` | 200 і оновлений Direction | `AdminOnly` + `PasswordChanged` |
| `PATCH /{id}/status` | 200 і новий status | `AdminOnly` + `PasswordChanged` |
| `PATCH /{id}/sort-order` | 200 і новий order | `AdminOnly` + `PasswordChanged` |
| `POST /{id}/archive` | 200 і archived state | `AdminOnly` + `PasswordChanged` |
| `POST /{id}/restore` | 200 і restored state | `AdminOnly` + `PasswordChanged` |

Validation повертає 400 Validation Problem Details. Для atomic `PUT` поля `name`, `code`, `status`, `sortOrder` і `version` є обов'язковими; пропущене поле не підміняється enum/int default і не змінює запис. Відсутній ID повертає 404. Duplicate code, stale `Version` і заборонена зміна архівованого запису повертають 409. Authentication/authorization middleware повертає 401 або 403 до виконання endpoint. `POST` повертає `201 Created` і створений DTO без `Location`: один і той самий internal endpoint доступний напряму та через Gateway з різними base paths, тому API не оголошує context-dependent URI.

## Persistence і concurrency

Таблиця `control_plane.directions` має primary key, unique index `ux_directions_code`, list index `ix_directions_list` та check constraints для status, normalized code, name length, sort order і timestamps. `xmin` не дублюється окремою колонкою: EF Core використовує PostgreSQL system column як concurrency token.

Кожна command виконує максимум один `SaveChangesAsync`; EF обгортає її зміни у transaction. Основний `PUT` атомарно оновлює `Name`, `Code`, `Description`, `Icon`, `Status` і `SortOrder` з однією expected `Version`; окремі status/sort endpoints залишаються quick actions списку. Попередня перевірка code дає зрозумілу помилку, а database unique index залишається остаточним захистом від конкурентного duplicate create/update. Stale update перетворюється на 409.

## Angular flow

Після authentication користувач відкриває `/directions`. Усі ролі можуть читати, фільтрувати й перегортати список; mutation controls рендеряться лише для `Admin`, а create/edit routes додатково захищені `roleGuard`. Edit form надсилає одну atomic mutation. Серверні policies залишаються остаточним джерелом доступу. Access token зберігається лише в пам’яті та додається чинним interceptor до same-origin API. Для кількох staggered `401` запит, відправлений зі старим token A після вже успішного refresh, повторюється з поточним token B без другого refresh. Якщо єдиний refresh не вдався, auth layer очищає memory-only session, прибирає дані protected view і один раз переходить на `/login`; пізні `401` старої сесії не запускають refresh або redirect повторно. `returnUrl` приймається лише як коректний local URL, не як external чи protocol-relative адреса.

[ControlPlane](../../src/Services/ControlPlane/README.md) · [Gateway](../../src/Gateway/AiControlCenter.Gateway/README.md) · [ADR](adr/0006-direction-lifecycle-and-access.md)
