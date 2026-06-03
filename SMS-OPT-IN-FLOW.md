# SMS Opt-In and Messaging Flow

This document describes the current SMS opt-in and message handling flow, including the status and columns updated in each step.

## Endpoints

- `POST /api/sms/opt-in` via `CreatePhoneOptIn`
- `POST /api/sms/send` via `SendSmsMessage`
- `POST /api/sms/reply` via `HandleSmsReply`

## Tables Involved

### `PhoneNumbers`
Columns used by this flow:

- `PhoneId`
- `PhoneNumber`
- `SmsOptInRequested`
- `SmsOptInConfirmed`
- `OptInRequestedDate`
- `OptInConfirmedDate`
- `Status` (`Pending`, `Active`, `Stopped`)
- `LeadFrom`

### `Messages`
Columns used by this flow:

- `MessageId`
- `PhoneId`
- `Direction` (`Inbound`, `Outbound`)
- `MessageText`
- `Timestamp`
- `DeliveryStatus` (`Queued`, `Received`, or provider-driven status)

## Flow 1: Phone Opt-In Request (`/sms/opt-in`)

1. Normalize `phoneNumber` to digits only.
2. Check if the phone already exists in `PhoneNumbers`.
3. If not found:
   - Insert a new row with:
     - `SmsOptInRequested = 1`
     - `SmsOptInConfirmed = 0`
     - `OptInRequestedDate = UTC now`
     - `OptInConfirmedDate = NULL`
     - `Status = 'Pending'`
4. If found but not confirmed/active:
   - Update row:
     - `SmsOptInRequested = 1`
     - `OptInRequestedDate = UTC now`
     - `Status = 'Pending'`
5. If not confirmed, queue an outbound opt-in prompt in `Messages`:
   - `Direction = 'Outbound'`
  - `MessageText = "Please opt in for messages from Forest Lake Auto Truck & Trailer. Reply 'YES' to confirm; data rates may apply. Reply STOP to unsubscribe."`
   - `DeliveryStatus = 'Queued'`

Result:
- Duplicate phone rows are avoided in this function.
- Existing numbers are reused and set back to `Pending` when needed.

## Flow 2: Send SMS (`/sms/send`)

1. Normalize `phoneNumber` and find/create `PhoneNumbers` row.
2. Check `SmsOptInConfirmed` and `Status`.
3. If not confirmed or not `Active`:
   - Queue opt-in prompt message instead of business message.
   - Ensure `Status = 'Pending'` and `SmsOptInRequested = 1`.
4. If confirmed and `Active`:
   - Queue requested business message.
5. Every outbound message is inserted into `Messages`.

Result:
- Outbound flow is centralized.
- Non-opted-in users are always prompted first.

## Flow 3: Inbound Reply Processing (`/sms/reply`)

1. Normalize `phoneNumber`, ensure `PhoneNumbers` row exists.
2. Insert inbound message into `Messages` with:
   - `Direction = 'Inbound'`
   - `DeliveryStatus = 'Received'`
3. Evaluate inbound keyword:
   - `YES` or `Y`:
     - Update `PhoneNumbers`:
       - `SmsOptInRequested = 1`
       - `SmsOptInConfirmed = 1`
       - `OptInConfirmedDate = UTC now`
       - `Status = 'Active'`
     - Queue confirmation outbound message in `Messages`.
   - `STOP`/`UNSUBSCRIBE`/`CANCEL`/`END`/`QUIT`:
     - Update `PhoneNumbers`:
       - `SmsOptInConfirmed = 0`
       - `OptInConfirmedDate = NULL`
       - `Status = 'Stopped'`
     - Queue unsubscribe outbound message in `Messages`.
   - Other text:
     - No opt-in status change.

Result:
- YES activates messaging.
- STOP blocks future business messages until YES is received again.

## Current Delivery Model

- Messages are recorded with `DeliveryStatus = 'Queued'` for outbound and `Received` for inbound.
- `POST /api/sms/dispatch` now sends queued outbound rows through Azure Communication Services.
- The dispatcher updates final `DeliveryStatus` to `Sent` or `Failed`.

## Suggested Next DB Hardening

1. Add a unique index on normalized phone number in `PhoneNumbers`.
2. Add an index on `Messages(PhoneId, Timestamp)` for conversation retrieval.
3. Add an optional `ProviderMessageId` column to `Messages` for delivery reconciliation.
