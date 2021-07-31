namespace Microsoft.eShopWeb
{
    public class OrderProcessingSettings
    {
        public string DeliveryOrderProcessorUrl { get; set; }

        public string ServiceBusConnectionString { get; set; }

        public string QueueName { get; set; }

    }
}
