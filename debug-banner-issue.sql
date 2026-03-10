-- Check for triggers on Units table
SELECT 
    t.name AS TriggerName,
    t.is_disabled AS IsDisabled,
    t.create_date AS CreateDate,
    t.modify_date AS ModifyDate,
    OBJECT_DEFINITION(t.object_id) AS TriggerCode
FROM sys.triggers t
WHERE t.parent_id = OBJECT_ID('[Units]');

-- Check the Banner column definition
SELECT 
    c.name AS ColumnName,
    TYPE_NAME(c.user_type_id) AS DataType,
    c.max_length AS MaxLength,
    c.is_nullable AS IsNullable,
    dc.name AS DefaultConstraintName,
    dc.definition AS DefaultValue
FROM sys.columns c
LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id AND c.object_id = dc.parent_object_id
WHERE c.object_id = OBJECT_ID('[Units]')
AND c.name IN ('Banner', 'UpdatedAt')
ORDER BY c.name;

-- Check recent updates to see what happened
SELECT TOP 20
    UnitID,
    VIN,
    Make,
    Model,
    Banner,
    UpdatedAt,
    CreatedAt
FROM [Units]
ORDER BY UpdatedAt DESC;
