# ADR 0006: Життєвий цикл і доступ до Direction

- Status: Accepted
- Date: 2026-09-06
- Scope: ControlPlane Directions

## Контекст

Напрямки мають створюватися через панель без зміни коду та зберігати стабільну ідентичність для майбутніх залежностей. Фізичне видалення, неузгоджене сортування або last-write-wins редагування зробили б такі посилання ненадійними.

## Рішення

- ControlPlane є єдиним власником `Direction` і таблиці `control_plane.directions`.
- `Code` — normalized lowercase slug і залишається унікальним також після архівування.
- Архівування є явною, відновлюваною доменною операцією; hard-delete endpoint відсутній.
- Архівований запис immutable до `Restore`.
- Список сортується за `SortOrder`, потім `Name` та `Id`.
- PostgreSQL `xmin` використовується для optimistic concurrency; клієнт передає `Version` у кожній mutation.
- `Admin` виконує mutations; `Admin`, `Developer` і `User` можуть читати. Для всіх операцій потрібна policy `PasswordChanged`.
- Gateway лише перевіряє edge policy і проксіює запит; ControlPlane повторно перевіряє JWT та policies.
- `Name` рахується як Unicode scalar values на клієнті, у .NET і PostgreSQL.
- Основна edit form використовує один atomic `PUT`; status/sort endpoints залишаються окремими quick actions.
- Список paged: default 20, maximum 100 записів на сторінку.

## Наслідки

Клієнт після кожної mutation використовує version із відповіді. Конкурентна зміна або duplicate code повертає 409; UI показує conflict, а актуальний стан отримується під час наступного явного завантаження або повторного відкриття форми. Archive зберігає рядок і code, тому створити новий Direction з code архівованого запису неможливо без попереднього рішення про перейменування.

Один `SaveChangesAsync` є transaction boundary кожної command. Унікальність захищена і application check, і database index; correctness не залежить від race-prone попередньої перевірки.

[Документація Directions](../directions.md) · [Межі сервісів](../service-boundaries.md) · [Security](../../security/security-overview.md)
