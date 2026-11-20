# POS staff notifications (issue #4)

The POS bell shows staff work that needs attention. Each alert also goes to the recipient's
Enterprise Portal inbox as a copy, through the shared notification service.

## What raises an alert

| Event | Who sees it | Occurrence (unique `StaffAlerts.OccurrenceKey`) |
| --- | --- | --- |
| Refund or void requested | Managers (or all-location roles) with **Order Management** at the order's branch, except the requester | `order-history:{id}`: the request's status-history row |
| Refund or void approved / rejected | The requester only, while they still have Order Management and the branch | `order-history:{id}`: the decision's status-history row |
| SCMS delivery manifest saved | Managers (or all-location roles) with **Stock Management** at the destination branch | `transfer-arrived:{transferId}` |
| Stock drops below its branch minimum | Managers (or all-location roles) with **Stock Management** at the branch | `low-stock:{location}:{variation}:{episode}` |

- Alerts are saved in the **same transaction** as the business change. Order alerts are raised in
  `OrderManagementService`. The manifest alert is raised in `TransfersController`. Low-stock
  alerts are raised by `PosDbContext` on every stock write, using `Stocks.LowStockAlertOpen` and
  `Stocks.LowStockEpisode`. A duplicate occurrence is skipped under a savepoint and never fails the
  business change.
- The requester's Portal ID is captured from the validated session when they file the request.
  Requests filed before this change fall back to `PosStaffUsers.AuthUserId`. When the requester
  can't be identified, the decision reaches no one. It is never broadcast to the branch.
- Reading `GET /api-pos/inventory/low-stock` has no notification side effects.
- The `AddStaffAlerts` migration marks existing shortages as already alerted, so the first stock
  write after deploy doesn't flood the bell.

## POS bell API (Portal `pos_session`)

All routes are under `/api-pos/notifications`. `PortalSessionMiddleware` validates the session
against both **Order Management** and **Stock Management**. A stock-only user sees stock alerts and
an order-only user sees order alerts. Every list, count and mark uses the viewer's current grants
and branches.

| Route | Purpose |
| --- | --- |
| `GET /api-pos/notifications?cursor=&limit=&unread=` | Newest first, `{ items, nextCursor, unread }` (limit 1–50, default 20) |
| `GET /api-pos/notifications/unread-count` | `{ unread }` |
| `POST /api-pos/notifications/{id}/read` | Marks one visible alert read. Another branch's alert returns 404 and stays unread |
| `POST /api-pos/notifications/read-all` | Marks the viewer's visible unread alerts read, and only their own Portal copies |
| `POST /api-pos/notifications/live-ticket` | `{ url }`: a short-lived `wss://…/notifications/live?ticket=…` for the shared `pos-staff` channel. Returns 503 when live updates are off, so the bell polls instead |

Each item carries `link`, a relative web-pos path:

- `/order-management?orderId=…`
- `/stock-management?tab=receive&transferId=…`
- `/stock-management?tab=levels&locationId=…&variationId=…`

Reading an alert never approves a refund or completes a transfer.

The live channel only carries a generic `pos.staff.changed` notice: no order, branch, customer or
amount. On connect, on reconnect and on each notice, the bell refetches the feed it is authorized
to see. Tickets and keys are never logged.

## Portal copies

`StaffAlertDispatcherBackgroundService` runs after each commit that creates or reads alerts, and
every 30 seconds otherwise.

1. **Audience.** On the first successful read of the Portal directory
   (`POST {PORTAL_URL}/api/integrations/recipients`), each alert's recipients are snapshotted once
   into `StaffAlertCopies`. An empty audience counts as resolved. A directory outage leaves the
   audience unresolved, and the alert stays in the POS bell.
2. **Delivery.** Each copy is retried on its own, with a 5-second deadline per call and backoff
   of 1, 2, 4 … 60 minutes. Unsent copies give up after 3 days.
   - Eligibility is rechecked before every unsent copy. If the recipient has lost access, POS only
     looks up an earlier copy by its idempotency key (`{kind}.{occurrence}`). It never creates one.
   - A replay or a lost response recovers the original remote UUID through
     `GET /notifications/notifications/lookup`.
3. **Read sync.** A POS read marks every linked copy read. When an eligible recipient reads
   their Portal copy, the alert is marked read and the other copies follow. A personal decision's
   read state belongs to its requester alone.

The Portal's Member bell filters for account/system notices. To verify Portal copies, use the
recipient-scoped lookup, or a Portal view that shows work alerts.

The directory lists explicit grants only. Super Admins and CEOs without an explicit POS grant
still see alerts in the POS bell, but they get no Portal copy.

## Configuration

| Variable | Meaning |
| --- | --- |
| `STAFF_ALERTS_ENABLED` | `true` sends Portal copies, read sync and refresh notices. `false` (the default) records alerts and serves the POS bell only |
| `POS_GATEWAY_URL`, `POS_GATEWAY_CALLER_KEY` | Shared with audit and file storage. The gateway must allow the "pos" Caller to create Portal copies, look them up and mark them read, and issue notification-inbox live tickets |
| `PORTAL_URL`, `PORTAL_SYSTEM_NAME`, `PORTAL_SYSTEM_SECRET` | The recipient directory (Basic auth as the POS System) |
| `POS_NOTIFICATIONS_SENDING_APP` | The gateway's registered sender name for POS, in exact case (default `pos`). Verify it in staging |
| `POS_NOTIFICATIONS_STAFF_CHANNEL` | The shared live channel (default `pos-staff`) |
| `POS_WEB_ORIGIN` | Origin for absolute links in Portal copies. Defaults to the first `WEB_POS_URL` entry |

## Rollout, pause and resume

1. Deploy with `RUN_DB_MIGRATIONS=true` and `STAFF_ALERTS_ENABLED=false`. Alerts start recording
   from new transitions only, and the POS bell works.
2. Confirm the gateway mapping and the web-pos proxy allowlist for `/api-pos/notifications/*`.
   Then set `STAFF_ALERTS_ENABLED=true` and restart.
3. The legacy `RefundService` still sends a SignalR `RefundApproved` to a location group. It only
   runs on the old `/api-pos/refunds` route, not the Order Management screen. Retire that consumer
   in web-pos when the bell ships, so the same refund never shows twice.

**Pause:** set `STAFF_ALERTS_ENABLED=false` and restart. Nothing goes out. Alerts keep being
recorded and served in POS.

**Resume:** set it back to `true` and restart. Unresolved audiences and pending copies are picked up
from the database. Check progress with:

```sql
SELECT "State", "Done", count(*), min("NextAttemptAt"), max("LastError")
FROM "StaffAlertCopies" GROUP BY 1, 2;
```

To retry everything now, run `UPDATE "StaffAlertCopies" SET "NextAttemptAt" = now() WHERE NOT "Done";`.

## Tests

`tests/ApiPos.Tests/StaffAlertTests.cs` covers the scenarios in the issue against a fake gateway.
`StaffAlertPostgresTests.cs` exercises the transaction, savepoint and migration baseline against a
real PostgreSQL. To run it, set `POS_TEST_POSTGRES` to a server connection string. It creates and
drops its own database.
