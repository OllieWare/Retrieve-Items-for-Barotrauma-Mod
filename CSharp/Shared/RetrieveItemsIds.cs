using Barotrauma;

namespace RetrieveItemsOrderMod
{
    public static class RetrieveItemsIds
    {
        public static readonly Identifier OrderIdentifier = "retrieveitems".ToIdentifier();
        public static readonly Identifier WreckOrderIdentifier = "retrievewreckitems".ToIdentifier();
        public static readonly Identifier MarkContainerOrderIdentifier = "markretrievecontainer".ToIdentifier();
        public static readonly Identifier MarkedContainerTag = "retrieveitemsmarked".ToIdentifier();
        public static readonly Identifier SearchDialog = "retrieveitems.searching".ToIdentifier();
        public static readonly Identifier ReturnDialog = "retrieveitems.returning".ToIdentifier();
        public static readonly Identifier DepositDialog = "retrieveitems.depositing".ToIdentifier();
        public static readonly Identifier AbortDialog = "retrieveitems.abort".ToIdentifier();
        public static readonly Identifier SevereInjuryDialog = "retrieveitems.abort.tooinjured".ToIdentifier();
        public static readonly Identifier NoTargetDialog = "retrieveitems.abort.notarget".ToIdentifier();
        public static readonly Identifier CannotStoreDialog = "retrieveitems.abort.nostorage".ToIdentifier();
        public static readonly Identifier DoneDialog = "retrieveitems.done".ToIdentifier();
        public static readonly Identifier OrderReceivedDialog = "retrieveitems.orderreceived".ToIdentifier();
        public static readonly Identifier CancelDialog = "retrieveitems.cancelled".ToIdentifier();
        public static readonly Identifier RefuseDialog = "retrieveitems.refused".ToIdentifier();
        public static readonly Identifier HostilesDialog = "retrieveitems.hostiles".ToIdentifier();
        public static readonly Identifier MarkedDialog = "retrieveitems.marked".ToIdentifier();
        public static readonly Identifier UnmarkedDialog = "retrieveitems.unmarked".ToIdentifier();
    }
}
