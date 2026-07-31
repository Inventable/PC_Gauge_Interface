namespace Gauge.Core;

public enum GaugeDiagnosticLevel
{
    Normal,
    Advisory,
    Warning
}

public sealed record GaugeDiagnosticEventDescription(
    ushort EventId,
    string Title,
    string Detail,
    GaugeDiagnosticLevel Level);

public static class V3DiagnosticEventCatalog
{
    public static GaugeDiagnosticEventDescription Describe(ushort eventId) =>
        eventId switch
        {
            0 => new(
                eventId,
                "No deployment event recorded",
                "The gauge diagnostic journal does not contain a previous deployment event.",
                GaugeDiagnosticLevel.Normal),
            1 => new(
                eventId,
                "Previous session recovered",
                "The gauge recovered its diagnostic journal after starting. Review any later event for the recorded session outcome.",
                GaugeDiagnosticLevel.Advisory),
            2 => Warning(eventId, "Operation timed out",
                "A gauge operation exceeded its permitted time."),
            3 => Warning(eventId, "Stored-data integrity event",
                "The gauge recorded a storage checksum or page-integrity problem."),
            4 => Warning(eventId, "Memory mirror degraded",
                "One external-memory copy could not be written or verified. The gauge requires service before deployment."),
            5 => new(
                eventId,
                "Sensor reading gap recorded",
                "The gauge detected a missing or delayed sensor reading during logging.",
                GaugeDiagnosticLevel.Advisory),
            6 => Warning(eventId, "Gauge restarted unexpectedly",
                "The watchdog restarted the gauge during a qualified recording session."),
            7 => new(
                eventId,
                "Low-power protection activated",
                "Supply voltage fell while the gauge was active. Further memory writes were inhibited to protect committed data.",
                GaugeDiagnosticLevel.Advisory),
            8 => Warning(eventId, "Sensor initialisation problem",
                "The attached sensor did not initialise normally."),
            9 => Warning(eventId, "Sensor communication problem",
                "The gauge recorded an invalid or incomplete sensor response."),
            10 => Warning(eventId, "Memory initialisation problem",
                "External memory did not initialise normally."),
            11 => Warning(eventId, "Recording operation failed",
                "The gauge could not complete a recording operation."),
            12 => new(
                eventId,
                "Logging stopped normally",
                "The previous recording was stopped in a controlled manner.",
                GaugeDiagnosticLevel.Normal),
            13 => new(
                eventId,
                "Power removed or logging stopped",
                "The previous recording ended after power was removed or logging otherwise stopped. This is normal operation and does not indicate a crash.",
                GaugeDiagnosticLevel.Normal),
            14 => Warning(eventId, "Logging stopped after a fault",
                "The gauge preserved a fault report from the previous qualified recording session."),
            _ => new(
                eventId,
                $"Gauge event {eventId}",
                "The gauge reported an event ID that this application version does not yet describe.",
                GaugeDiagnosticLevel.Advisory)
        };

    private static GaugeDiagnosticEventDescription Warning(
        ushort eventId,
        string title,
        string detail) =>
        new(eventId, title, detail, GaugeDiagnosticLevel.Warning);
}
