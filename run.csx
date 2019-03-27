#r "Newtonsoft.Json"
#r "CoreTweet.dll"
#r "Microsoft.WindowsAzure.Storage"

#load "tableentity.csx"
#load "product.csx"

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using CoreTweet;

const string DOMAIN = "http://www.aladin.co.kr/";

const int CATEGORY_ID_COMICS = 2551;
const int CATEGORY_ID_LNOVEL = 50927;
const int CATEGORY_ID_ITBOOK = 351;

static async Task<ProductList> FetchProductListAsync(HttpClient client, bool eBook, int categoryId, ILogger log)
{
    string ttbKey = Environment.GetEnvironmentVariable("TTB_KEY");
    string partnerId = Environment.GetEnvironmentVariable("PARTNER_ID");

    Dictionary<string, string> queryDict = new Dictionary<string, string>();
    queryDict.Add("querytype", "itemnewall");
    queryDict.Add("version", "20131101");
    queryDict.Add("cover", "big");
    queryDict.Add("output", "js");
    queryDict.Add("maxresults", "30");
    queryDict.Add("searchtarget", eBook ? "ebook" : "book");
    queryDict.Add("optresult", "ebooklist,fileformatlist");
    queryDict.Add("categoryid", categoryId.ToString());
    queryDict.Add("ttbkey", ttbKey);
    queryDict.Add("partner", partnerId);

    StringBuilder sb = new StringBuilder(256);
    sb.Append("ttb/api/itemlist.aspx?");
    foreach (var kvp in queryDict)
    {
        sb.Append(kvp.Key).Append("=").Append(kvp.Value).Append("&");
    }
    
    Uri uri = new Uri(new Uri(DOMAIN), sb.ToString());

    log.LogInformation("target uri: " + uri);

    string res = await client.GetStringAsync(uri);
    return JsonConvert.DeserializeObject<ProductList>(res);
}

static async Task AddToTable(IAsyncCollector<BookEntity> outputTable, string key, Product product)
{
    BookEntity newEntity = new BookEntity();
    newEntity.PartitionKey = key;
    newEntity.RowKey = product.itemId.ToString();
    newEntity.Name = product.title;

    await outputTable.AddAsync(newEntity);
}

static async Task TweetProduct(Tokens tokens, HttpClient client, string key, Product product)
{
    Stream stream = await client.GetStreamAsync(product.cover);
    MediaUploadResult mediaUploadResult = await tokens.Media.UploadAsync(stream);

    long[] mediaIds = { mediaUploadResult.MediaId };
    string status = product.ToString();
    StatusResponse updateResponse = await tokens.Statuses.UpdateAsync(status, media_ids: mediaIds);

    if (product.subInfo.ebookList != null && product.subInfo.ebookList.Count > 0)
    {
        SubBook subBook = product.subInfo.ebookList[0];
        string link = subBook.link.Replace(@"\\/", @"/").Replace(@"&amp;", @"&");
        status = $"[전자책] {product.title} ({subBook.priceSales}원) {link}";
        await tokens.Statuses.UpdateAsync(status, updateResponse.Id/*, media_ids: mediaIds*/);
    }
    else if (product.subInfo.paperBookList != null && product.subInfo.paperBookList.Count > 0)
    {
        SubBook subBook = product.subInfo.paperBookList[0];
        string link = subBook.link.Replace(@"\\/", @"/").Replace(@"&amp;", @"&");
        status = $"[종이책] {product.title} ({subBook.priceSales}원) {link}";
        await tokens.Statuses.UpdateAsync(status, updateResponse.Id/*, media_ids: mediaIds*/);
    }
}

static async Task TweetProductList(
    HttpClient client,
    bool eBook,
    int categoryId,
    string key,
    CloudTable cloudTable,
    IAsyncCollector<BookEntity> outputTable,
    ILogger log)
{
    ProductList productList = await FetchProductListAsync(client, false, categoryId, log);

    string consumerKey = Environment.GetEnvironmentVariable(key + "_CONSUMER_KEY");
    string consumerSecret = Environment.GetEnvironmentVariable(key + "_CONSUMER_SECRET");
    string accessToken = Environment.GetEnvironmentVariable(key + "_ACCESS_TOKEN");
    string accessTokenSecret = Environment.GetEnvironmentVariable(key + "_ACCESS_TOKEN_SECRET");

    Tokens tokens = Tokens.Create(consumerKey, consumerSecret, accessToken, accessTokenSecret);

    foreach (Product product in productList.item)
    {
        try
        {
            TableOperation retrieveOperation = TableOperation.Retrieve<BookEntity>(key, product.itemId.ToString());
            TableResult retrievedResult = await cloudTable.ExecuteAsync(retrieveOperation);
            if (retrievedResult.Result == null)
            {
                log.LogInformation($"adding {product.ToString()}");

                Task tweetTask = TweetProduct(tokens, client, key, product);
                Task tableTask = AddToTable(outputTable, key, product);
                
                await tweetTask;
                await tableTask;
            }
        }
        catch (Exception e)
        {
            log.LogError(e.Message);
        }
    }
}

public static async Task Run(TimerInfo myTimer,
    CloudTable cloudTable,
    IAsyncCollector<BookEntity> outputTable,
    ILogger log)
{
    log.LogInformation($"Execution Time: {DateTime.Now}");

    HttpClient client = new HttpClient();

    Task comicsTask = TweetProductList(client, false, CATEGORY_ID_COMICS, "COMICS", cloudTable, outputTable, log);
    Task lnovelTask = TweetProductList(client, false, CATEGORY_ID_LNOVEL, "LNOVEL", cloudTable, outputTable, log);
    Task itbookTask = TweetProductList(client, false, CATEGORY_ID_ITBOOK, "ITBOOK", cloudTable, outputTable, log);

    await comicsTask;
    await lnovelTask;
    await itbookTask;
}
