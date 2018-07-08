#r "Newtonsoft.Json"
#r "CoreTweet.dll"

#load "entity.csx"

using System;
using System.Net.Http;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Newtonsoft.Json;
using CoreTweet;

const string DOMAIN = "http://www.aladin.co.kr/";

const int CATEGORY_ID_COMICS = 2551;
const int CATEGORY_ID_LNOVEL = 50927;
const int CATEGORY_ID_ITBOOK = 351;

static async Task<BookList> Request(HttpClient client, int categoryId, TraceWriter log)
{
    string ttbKey = Environment.GetEnvironmentVariable("TTB_KEY");
    string partnerId = Environment.GetEnvironmentVariable("PARTNER_ID");
    
    Uri uri = new Uri(new Uri(DOMAIN), $"ttb/api/itemlist.aspx?querytype=itemnewall&searchtarget=book&version=20131101&cover=big&output=js&maxresults=30&categoryid={categoryId}&ttbkey={ttbKey}&partner={partnerId}");

    log.Info("target uri: " + uri);

    string res = await client.GetStringAsync(uri);
    return JsonConvert.DeserializeObject<BookList>(res);
}

static async Task TweetBook(string key, HttpClient client, BookList bookList, IQueryable<BookEntity> inputTable, IAsyncCollector<BookEntity> outputTable, TraceWriter log)
{
    string consumerKey = Environment.GetEnvironmentVariable(key + "_CONSUMER_KEY");
    string consumerSecret = Environment.GetEnvironmentVariable(key + "_CONSUMER_SECRET");
    string accessToken = Environment.GetEnvironmentVariable(key + "_ACCESS_TOKEN");
    string accessTokenSecret = Environment.GetEnvironmentVariable(key + "_ACCESS_TOKEN_SECRET");

    Tokens tokens = Tokens.Create(consumerKey, consumerSecret, accessToken, accessTokenSecret);

    IEnumerable<BookEntity> filteredTable = inputTable.Where(entity => entity.PartitionKey == key);

    foreach (Book book in bookList.item)
    {
        bool exist = false;
        foreach (BookEntity entity in filteredTable)
        {
            if (entity.PartitionKey == key && int.Parse(entity.RowKey) == book.itemId)
            {
                exist = true;
                break;
            }
        }

        if (exist)
        {
            continue;
        }

        try
        {
            string status = book.ToString();
            log.Info($"adding {status}");

            Stream stream = await client.GetStreamAsync(book.cover);
            await tokens.Statuses.UpdateWithMediaAsync(status, stream);

            BookEntity newEntity = new BookEntity();
            newEntity.PartitionKey = key;
            newEntity.RowKey = book.itemId.ToString();
            newEntity.Name = book.title;

            await outputTable.AddAsync(newEntity);
            //break;
        }
        catch (Exception e)
        {
            log.Error(e.Message);
        }
    }
}

public static async Task Run(TimerInfo myTimer, IQueryable<BookEntity> inputTable, IAsyncCollector<BookEntity> outputTable, TraceWriter log)
{
    HttpClient client = new HttpClient();
    
    log.Info($"Excution Time: {DateTime.Now}");

    BookList bookList = await Request(client, CATEGORY_ID_COMICS, log);
    await TweetBook("COMICS", client, bookList, inputTable, outputTable, log);

    bookList = await Request(client, CATEGORY_ID_LNOVEL, log);
    await TweetBook("LNOVEL", client, bookList, inputTable, outputTable, log);

    bookList = await Request(client, CATEGORY_ID_ITBOOK, log);
    await TweetBook("ITBOOK", client, bookList, inputTable, outputTable, log);
}
