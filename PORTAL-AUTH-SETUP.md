# POS location APIs and Enterprise Portal

The location-management APIs validate the `pos_session` cookie with the
Enterprise Portal on every request. The portal must have POS registered with
the deployed and local callbacks, and `PORTAL_SYSTEM_SECRET` must match that
registration.

The staff-assignment list reads active users with at least one active POS
module grant from the portal PostgreSQL database. Configure `PORTAL_DATABASE_URL`
with a database role that has `SELECT` access only to `User`, `Role`,
`AccessGrant`, `Module`, and `System`. Each directory query also runs in a
read-only transaction. Do not use an owner credential.

## Local development

Set these in `api-pos/.env` (the file is git-ignored):

- `PORTAL_URL=http://localhost:3000`
- `PORTAL_SYSTEM_NAME=POS`
- `PORTAL_SYSTEM_SECRET` to the same local POS integration secret configured in
  `web-pos/.env.local`
- `PORTAL_DATABASE_URL` to the read-only connection for the local/mirror Portal
  database

Run api-pos on port 5005 and web-pos on HTTPS port 3002. The Next.js API routes
forward only the HttpOnly `pos_session` cookie to api-pos.

## Deployment

Set `PORTAL_SYSTEM_SECRET` and `PORTAL_DATABASE_URL` as secret environment
variables on the api-pos host. Set `PORTAL_URL` to the deployed Portal origin
and `PORTAL_SYSTEM_NAME=POS`. Set `API_POS_URL` on web-pos to the api-pos
origin. Keep the secret and database credential out of source control and
browser-prefixed environment variables.
