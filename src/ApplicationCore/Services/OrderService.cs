using Ardalis.GuardClauses;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Microsoft.Azure.ServiceBus;
using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Text;
using System.Net.Http;

namespace Microsoft.eShopWeb.ApplicationCore.Services
{

    public delegate void Notify(Order order);

    public class OrderService : IOrderService
    {

        private readonly IAsyncRepository<Order> _orderRepository;
        private readonly IUriComposer _uriComposer;
        private readonly IAsyncRepository<Basket> _basketRepository;
        private readonly IAsyncRepository<CatalogItem> _itemRepository;
        private readonly OrderProcessingSettings _orderProcessingSettings;

        static IQueueClient queueClient;

        public event Notify OrderSubmitted;

        public OrderService(IAsyncRepository<Basket> basketRepository,
            IAsyncRepository<CatalogItem> itemRepository,
            IAsyncRepository<Order> orderRepository,
            IUriComposer uriComposer,
            OrderProcessingSettings orderProcessingSettings)
        {
            _orderRepository = orderRepository;
            _uriComposer = uriComposer;
            _basketRepository = basketRepository;
            _itemRepository = itemRepository;
            _orderProcessingSettings = orderProcessingSettings;

            var policy = new RetryExponential(
                minimumBackoff: TimeSpan.FromSeconds(10),
                maximumBackoff: TimeSpan.FromSeconds(30),
                maximumRetryCount: 3
            );

            queueClient = new QueueClient(orderProcessingSettings.ServiceBusConnectionString, orderProcessingSettings.QueueName, ReceiveMode.PeekLock, policy); 

            OrderSubmitted = OnOrderSubmitted;
        }

        public async Task CreateOrderAsync(int basketId, Address shippingAddress)
        {
            var basketSpec = new BasketWithItemsSpecification(basketId);
            var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

            Guard.Against.NullBasket(basketId, basket);
            Guard.Against.EmptyBasketOnCheckout(basket.Items);

            var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
            var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

            var items = basket.Items.Select(basketItem =>
            {
                var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
                var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
                var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
                return orderItem;
            }).ToList();
            
            var order = new Order(basket.BuyerId, shippingAddress, items);

            await _orderRepository.AddAsync(order);

            OrderSubmitted?.Invoke(order);
        }

        public async void OnOrderSubmitted(Order order)
        {
            string messageBody = JsonConvert.SerializeObject(order);

            // Trigger Delivery Order Process function app.
            using (var httpClient = new HttpClient())
            {
                var functionUrl = _orderProcessingSettings.DeliveryOrderProcessorUrl;
                var content = new StringContent(messageBody, Encoding.UTF8, "application/json");
                await httpClient.PostAsync(functionUrl, content);
            }

            // Add message to Service Bus queue.
            try
            {
                var message = new Message(Encoding.UTF8.GetBytes(messageBody))
                {
                    Body = Encoding.UTF8.GetBytes(messageBody),
                    ContentType = "application/json"
                };

                await queueClient.SendAsync(message);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"{DateTime.Now} :: Exception: {exception.Message}");
            }

            await queueClient.CloseAsync();
        }
    }
}
