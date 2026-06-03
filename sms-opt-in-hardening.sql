/*
SMS opt-in hardening script
- Normalizes existing PhoneNumbers.PhoneNumber values to digits-only where possible.
- Reports any duplicates that must be manually resolved.
- Adds indexes to prevent duplicate phone entries and speed message lookups.
*/

SET NOCOUNT ON;

-- 1) Normalize existing phone values where they include punctuation/spaces.
--    This keeps only digits and strips a leading U.S. country code '1' when length is 11.
;WITH RawPhones AS (
    SELECT
        PhoneId,
        PhoneNumber,
        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
            ISNULL(PhoneNumber, ''),
            '(', ''), ')', ''), '-', ''), ' ', ''), '+', ''), '.', ''), '/', ''), '\\', ''), CHAR(9), ''), CHAR(13), '') AS Digits
    FROM dbo.PhoneNumbers
)
UPDATE pn
SET PhoneNumber = CASE
    WHEN LEN(rp.Digits) = 11 AND LEFT(rp.Digits, 1) = '1' THEN RIGHT(rp.Digits, 10)
    ELSE rp.Digits
END
FROM dbo.PhoneNumbers pn
INNER JOIN RawPhones rp ON rp.PhoneId = pn.PhoneId
WHERE pn.PhoneNumber IS NOT NULL
  AND pn.PhoneNumber <> CASE
    WHEN LEN(rp.Digits) = 11 AND LEFT(rp.Digits, 1) = '1' THEN RIGHT(rp.Digits, 10)
    ELSE rp.Digits
END;

-- 2) Detect duplicates that would block unique index creation.
SELECT
    PhoneNumber,
    COUNT(*) AS DuplicateCount,
    MIN(PhoneId) AS KeepPhoneId,
    MAX(PhoneId) AS LastPhoneId
FROM dbo.PhoneNumbers
WHERE PhoneNumber IS NOT NULL
GROUP BY PhoneNumber
HAVING COUNT(*) > 1
ORDER BY DuplicateCount DESC, PhoneNumber;

-- 3) Add unique index after duplicates are cleaned.
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'UX_PhoneNumbers_PhoneNumber'
      AND object_id = OBJECT_ID('dbo.PhoneNumbers')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_PhoneNumbers_PhoneNumber
        ON dbo.PhoneNumbers(PhoneNumber)
        WHERE PhoneNumber IS NOT NULL;
END;

-- 4) Add message lookup index for conversation history and polling.
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Messages_PhoneId_Timestamp'
      AND object_id = OBJECT_ID('dbo.Messages')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Messages_PhoneId_Timestamp
        ON dbo.Messages(PhoneId, [Timestamp] DESC);
END;
