namespace RetrieveItemsOrderMod
{
    internal static class MarkContainerChatRelayPatch
    {
        public static bool Prefix(object __0)
        {
            if (!RetrieveItemsOrderRules.TryHandleMarkContainerRelay(__0))
            {
                return true;
            }

            // LuaCsLogger.Log("[RetrieveItemsOrder] Consumed hidden mark relay chat message");
            return false;
        }
    }
}
