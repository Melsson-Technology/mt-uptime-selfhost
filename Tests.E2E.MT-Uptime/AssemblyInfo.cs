// The E2E assembly runs its tests one at a time, and both reasons are load-bearing.
//
// 1. The target services are shared and singular. There is one MySQL, one DNS resolver, one HTTP
//    fixture and one break/restore helper on the box. A test class calling `restore http` while
//    another is still asserting Down is not a race that can be tuned around — it is two tests giving
//    one service contradictory instructions.
//
// 2. Incidents correlate by host, and on this box every host is 127.0.0.1. CorrelationKeyResolver
//    keys HTTP, TCP and database monitors by the address they depend on, so every one of them shares
//    the key `ip:127.0.0.1`, and IncidentService joins failures on the same key inside a ten-minute
//    window into ONE incident. Two scenarios failing concurrently would therefore see each other's
//    monitors in `otherAffectedMonitors` and a monitorCount neither of them set up. (DNS monitors key
//    on their resolver instead, so they correlate separately.)
//
// The cost is wall-clock: the battery is minutes, not seconds. That is the right trade for a suite
// whose whole purpose is to be believable.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
