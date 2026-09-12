SET NOCOUNT ON;
IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory
    WHERE MigrationId = N'20260911020456_AddInfobipMessagingAndWhatsAppConsent')
    THROW 51002, 'WhatsApp migration is missing from EF history.', 1;
IF OBJECT_ID(N'dbo.NotificationDeliveries', N'U') IS NULL
    OR OBJECT_ID(N'dbo.UserCommunicationConsents', N'U') IS NULL
    OR ISNULL(COL_LENGTH(N'dbo.AspNetUsers', N'NormalizedWhatsAppPhoneNumber'), -1) <> 32
    OR ISNULL(COL_LENGTH(N'dbo.AspNetUsers', N'PendingWhatsAppPhoneNumber'), -1) <> 32
    OR ISNULL(COL_LENGTH(N'dbo.AspNetUsers', N'WhatsAppPhoneNumber'), -1) <> 32
    OR COL_LENGTH(N'dbo.AspNetUsers', N'UsePrimaryPhoneForWhatsApp') IS NULL
    OR COL_LENGTH(N'dbo.RentReminders', N'WhatsAppRequested') IS NULL
    OR COL_LENGTH(N'dbo.ApartmentRentReminderRules', N'WhatsAppEnabled') IS NULL
    THROW 51003, 'WhatsApp tables or columns are missing or incorrect.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AspNetUsers')
    AND name = N'IX_AspNetUsers_NormalizedWhatsAppPhoneNumber' AND is_unique = 1 AND has_filter = 1 AND is_disabled = 0)
    OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.NotificationDeliveries')
    AND name = N'IX_NotificationDeliveries_IdempotencyKey' AND is_unique = 1 AND is_disabled = 0)
    OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.UserCommunicationConsents')
    AND name = N'IX_UserCommunicationConsents_UserId_Channel_Purpose' AND is_unique = 1 AND has_filter = 1 AND is_disabled = 0)
    THROW 51004, 'A required WhatsApp uniqueness/idempotency index is missing or disabled.', 1;
SELECT MigrationId FROM dbo.__EFMigrationsHistory
WHERE MigrationId = N'20260911020456_AddInfobipMessagingAndWhatsAppConsent';
PRINT 'WhatsApp schema verified: migration, columns and unique indexes.';
