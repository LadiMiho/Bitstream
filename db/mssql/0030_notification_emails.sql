/*
    0030_notification_emails.sql
    Email notifications of request status changes, next to the in-app ones (0029):
      * portal.NotificationRule.EmailEnabled: whether the status change emails that audience.
        When the column is first added it is switched on wherever the in-app notification is, so
        email starts with the same choices; change them on Access Management > Notification
        settings.
      * The mails are queued in ops.Notification (0004) when the status changes and sent by the
        API host through the email gateway (Integration:Email); their wording is in
        Notifications:Email.
    Safe to re-run: the copy from IsEnabled only happens when the column is created. Schema
    version 23.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('portal.NotificationRule', 'EmailEnabled') IS NULL
BEGIN
    ALTER TABLE portal.NotificationRule
        ADD EmailEnabled bit NOT NULL CONSTRAINT DF_NotificationRule_EmailEnabled DEFAULT (0);

    -- The column does not exist when this batch is compiled, hence the dynamic statement.
    EXEC (N'UPDATE portal.NotificationRule SET EmailEnabled = IsEnabled;');
END;
GO

-- The dispatcher reads pending mails by status and age; 0004 already indexes (Status, CreatedAt).
