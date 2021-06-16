# Azure Functions on K8s

## Introduction

This project provides:

1. a simple Azure Function with a queue listener, and a HTTP function to add messages to the queue
2. a set of instructions for deploying the function to a k8s cluster, optionally targeting AKS
3. a demo of how the KEDA queue-based auto-scaling works

## Creating a function with a Docker file

This first section explains how to create a Docker enabled function.

You will need to have the Azure Function CLI installed to use the `func` tools. [Instructions to install the Azure Functions Core Tools installation instructions can be found here](https://docs.microsoft.com/en-us/azure/azure-functions/functions-run-local?tabs=windows%2Ccsharp%2Cbash#install-the-azure-functions-core-tools), and [instructions for creating a k8s enabled function can be found here](https://docs.microsoft.com/en-us/azure/azure-functions/functions-kubernetes-keda). We will deep-dive into the end-to-end workflow for creating such a function app. 

The commands in this documentation are similar to what you will find in the [Azure Functions command line quick-start](https://docs.microsoft.com/en-us/azure/azure-functions/create-first-function-cli-csharp?tabs=azure-cli%2Cbrowser&source=docs), but are specific for creating a Dockerised function app. I would like to acknowledge that I have borrowed some of the [material from the Azure team blog](https://medium.com/microsoftazure/lifting-function-to-kubernetes-with-keda-e24de86fca2e)

If you are developing in VS Code it is helpful to install the [Azure Functions extensions](https://docs.microsoft.com/en-us/azure/azure-functions/functions-develop-vs-code?tabs=csharp#install-the-azure-functions-extension).

Once you have installed the tools, you can initialize a new Azure Functions project - with Docker support - by running the following command:

`func init ReviewsWorkerFunctionApp --dotnet --docker`

The `./ReviewsWorkerFunctionApp` sub-directory will be generated via the previous command, containing the scaffolding for a function app. Use:

`cd ./ReviewsWorkerFunctionApp`

...to change to this directory.

For the purposes of this demo, we will create a simple Azure Storage Queue listener. We can add a new storage queue listener function with the following command from within:

`func new`

A list of template options will be provided - choose:
 `QueueTrigger`

...and for the function name we will use:
`ReviewQueueListener`

The purposes of this function will be to listen to messages from a reviews queue, which we will populate in a moment. Function triggers like the queue-listener may require special scaling metrics - such as the [KEDA queue storage scaler](https://keda.sh/docs/1.4/scalers/azure-storage-queue/) - while HTTP triggered functions can leverage the default Horizontal Pod Autoscaling metrics for a web API running under Kubernetes.

Because we are building a queue storage trigger, we will need an Azure Storage Queue. Note that we can target other types of queues and event streams with KEDA including:

- [Azure Service Bus](https://keda.sh/docs/1.4/scalers/azure-service-bus/)
- [RabbitMq](https://keda.sh/docs/1.4/scalers/rabbitmq-queue/)
- [Apache Kafka](https://keda.sh/docs/1.4/scalers/apache-kafka/)
- [AWS Kinesis](https://keda.sh/docs/1.4/scalers/aws-kinesis/)

## Creating the Azure Storage Account

To get started, we will [create an Azure Storage Queue using the Azure CLI](https://docs.microsoft.com/en-us/azure/storage/common/storage-account-create?tabs=azure-cli). If you prefer, you can also [create an Azure Storage Queue via the portal](https://docs.microsoft.com/en-us/azure/storage/queues/storage-quickstart-queues-portal). Make sure you login:

`az login`

I'm assuming you [already have an Azure subscription](https://docs.microsoft.com/en-us/cli/azure/manage-azure-subscriptions-azure-cli). Find the target subscription with:

`az account list -o table`

You will be presented with a table of subscriptions. Make sure `IsDefault` is true for the target subscription, otherwise use the following command to set the subscription:

`az account set --subscription <subscription-id-here>`

Replace `<subscription-id-here>` with the relevant subscription Id from the output of the list command.

We'll create a new resource-group for our resources. Set a variable for the resource-group name, as we'll need to use this a few times:

`group=reviews-rg`

You'll also need to choose a location to host your resources. You can list the available locations with:

`az account list-locations -o table`

Again, set a variable with:

`location=australiaeast`

Create the resource-group with:

`az group create --name $group --location $location`

The final command will create the Azure Storage Account. We will first set a few variables to help us understand the various options:

1) the name of the parent storage account for the queue
`storageAccountName=reviewsonk8storage`

2) the `kind` of the account - typically you will want `StorageV2` at the time of writing:
`storageKind=StorageV2`

3) the (storage-account SKU)[storageKind]. As I'm only using this for a demo, I've selected "Locally Redundant Storage" as it is the lowest cost option:
`storageSku=Standard_LRS`

4) access-tier is `hot` as we will be querying the data frequently:
`storageAccessTier=hot`

5) I've set `https-only` to true, so that only requests sent over HTTPS are supported:
`storageHttpsOnly=true`

The command to create the storage-account will then be:

`az storage account create --resource-group $group --name $storageAccountName --location $location --kind $storageKind --sku $storageSku --access-tier $storageAccessTier --https-only $storageHttpsOnly`

Finally we need to create the storage queue inside the new storage account. [The queue creation parameters are explained here](https://docs.microsoft.com/en-us/cli/azure/storage/queue?view=azure-cli-latest#az_storage_queue_create), but we will go through them.

We will call our storage queue `review-submitted`:

`queueName=review-submitted`

We also need a storage-account key to use with the queue. We will generate this using the following command and store the output in a variable for later use:

`storageAccountKey=$(az storage account keys list --resource-group $group --account-name $storageAccountName --query "[0].value" | tr -d '"')`

We will also grab a connection string which we will need to use with our function app in a later section:

`storageAccountConnectionString="DefaultEndpointsProtocol=https;AccountName=$storageAccountName;AccountKey=$storageAccountKey;EndpointSuffix=core.windows.net"`

Use `echo $storageAccountConnectionString` to output the connection string and make note of it for later. 

We can then create the new storage queue using the generated account key and connection string:

`az storage queue create --name $queueName --account-key $storageAccountKey --account-name $storageAccountName --connection-string $storageAccountConnectionString`

## Connect the function to the queue

We will now make some small changes to the function we generated earlier to connect it to the queue.

In the function code: `./ReviewsWorkerFunctionApp/ReviewQueueListener.cs`

...add the queue-name and a new alias for the connection string, i.e.

`public static void Run([QueueTrigger("myqueue-items", Connection = "")]string myQueueItem, ILogger log)`

...needs to be changes to:

`public static void Run([QueueTrigger("review-submitted", Connection = "ReviewQueueConnectionString")]string myQueueItem, ILogger log)`

Note that this means we need the running environment of the function app to provide a connection-string called `ReviewQueueConnectionString`. We'll address this in the next section.

## Connecting to the queue and adding messages

So that we can test the scaling features of KEDA, we're going to add a 15 second delay to the processing of the message. Update the function body to be an async task and add a delay, i.e.:

```
using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;

namespace ReviewsWorkerFunctionApp
{
    public static class ReviewQueueListener
    {
        [FunctionName("ReviewQueueListener")]
        public static async Task Run(
            [QueueTrigger(
                "review-submitted",
                Connection = "ReviewQueueConnectionString")]
            string myQueueItem,
            ILogger log)
        {
            log.LogInformation($"Started processing at {DateTime.UtcNow:o}. Message: {nameof(ReviewQueueListener)}");

            // Wait for 15 seconds to allow messages to
            // build up in the queue.
            await Task.Delay(15 * 1000);

            log.LogInformation($"Finished processing at {DateTime.UtcNow:o}. Message: {nameof(ReviewQueueListener)}");
        }
    }
}
```

If you now build your function app with `.NET build`, you'll get a warning about a missing connection property in the `local.settings.json` file. Make sure `local.settings.json` is included in your .gitignore file so we don't commit credentials to the Git repo, and then go ahead and add the connection string to `local.settings.json`:

```
{
    "IsEncrypted": false,
    "Values": {
        "ReviewQueueConnectionString": "<YOUR-CONNECTION-STRING-HERE>",
        "AzureWebJobsStorage": "<YOUR-CONNECTION-STRING-HERE>",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet"
    }
}
```

Replace `<YOUR-CONNECTION-STRING-HERE>` with the value of `echo $storageAccountConnectionString`. Note that the name of the connection string `ReviewQueueConnectionString` corresponds to the connection name stored against the queue trigger. If you build now the warning should go away.

To quickly test that the function now runs, use:

`func start`

The function should start up and list the `ReviewQueueListener` as available.

Before we continue, we'll need some way to add reviews to our review queue. Let's add a new HTTP triggered function that adds a randomly generated review to the queue with:

`func new`

For type select:
`HttpTrigger`.

For name enter:
`ReviewGenerator`

We're going to add the [`bogus` package](https://github.com/bchavez/Bogus) to the project. This will allow us to generate a random review in the new function. Use:

`dotnet add package Bogus --version 33.0.2`

I added an output binding to write the generated review to the queue. You can find all of this code in the `./src` sub-directory, but here is the rough code:

```
using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Bogus;
using ReviewsWorkerFunctionApp.Utilities;
using ReviewsWorkerFunctionApp.Models;

namespace ReviewsWorkerFunctionApp
{
    public static class ReviewGenerator
    {
        [FunctionName(nameof(ReviewGenerator))]
        public static IActionResult Run(
            [HttpTrigger(
                AuthorizationLevel.Anonymous,
                "get",
                "post",
                Route = "review")] HttpRequest req,
            ILogger log,
            [Queue(
                queueName: QueueHelper.REVIEW_QUEUE_NAME,
                Connection = QueueHelper.REVIEW_QUEUE_CONNECTION_STRING_NAME)] out string reviewMessage)
        {
            log.LogInformation($"Generating review with {nameof(ReviewGenerator)}.");

            var faker = new Faker("en");

            var messageString = JsonConvert.SerializeObject(
                new ReviewModel
                {
                    EventId = Guid.NewGuid().ToString(),
                    SubjectId = Guid.NewGuid().ToString(),
                    EventType = "ReviewSubmitted",
                    Content = new Review {
                        Text = faker.Rant.Review()
                    }
                });

            reviewMessage = messageString;

            log.LogInformation($"Sending review: {messageString}");

            return new OkObjectResult(messageString);
        }
    }
}
```
**Note**: I have made function access anonymous to make the demo simpler when testing via Docker.

## Creating a private Docker repository with ACR

Unless you want to push your Docker image to a public image repository, you'll need to create a private Docker registry. For this purpose, I will use ACR (Azure Container Registry). Below are the commands to provision the registry.

First, declare a name for the registry:

`containerRegistryName=reviewsContainerRegistryDemo`

The following command will create the container registry. I have created it in the same group for the convenience of being able to delete the whole group after the demo. I'll use the (`Basic` SKU)[https://docs.microsoft.com/en-us/azure/container-registry/container-registry-skus] for the purposes of the demo:

`az acr create --resource-group $group --name $containerRegistryName --sku Basic`

The next command assigns a system managed identity to the container-registry:

`az acr identity assign --identities [system] --name $containerRegistryName`

Use the following command to print out details of the ACR login server etc. You'll need them for the next section:

`az acr list -o table`

## Build the Docker image and push to ACR

The next step is to build the Docker image and push it to ACR (Azure Container Registry). You'll need the ACR login server from the output of the last command in the previous section. The format of the command will be:

`loginServer=reviewscontainerregistrydemo.azurecr.io`

Note that your container registry name will be different. To build the container run:

`reviewsDockerImageName=$loginServer/reviews-processor:latest`

`docker build -t $reviewsDockerImageName .`

Finally, we need to push the built image to ACR. Before we can push to ACR, we need to authenticate:

`az acr login --name $containerRegistryName`

Now that we have authenticated with ACR, we can push images to it but using the fully-qualified "login-server" path:

`docker push $reviewsDockerImageName`

To see the list of images under the ACR account run:

`az acr repository list --name $containerRegistryName --output table`

The Docker image we have just produced should run anywhere Docker runs, e.g. you can run it locally with:

`docker run -p 5050:80 -e AzureWebJobsStorage=$storageAccountConnectionString -e ReviewQueueConnectionString=$storageAccountConnectionString $reviewsDockerImageName`

You should then be able to reach the `api/review` endpoint via port `5050` on your local machine:

`http://127.0.0.1:5050/api/review`

Don't forget to kill the running container. I use:

`docker container list`

...to list the running containers and:

`docker container kill <container-id-here>`

...to kill the relevant container by it's ID from the previous command.

## (Optional) Create an AKS cluster and grab the credentials

If you already have a Kubernetes cluster running via Docker Desktop or MiniKube, you may choose to skip the creation of an AKS cluster. If you do, I'll assume you already have the credentials ready for your local/test cluster. Also note that you will need to [connect your cluster up to ACR](https://docs.microsoft.com/en-us/azure/container-registry/container-registry-auth-kubernetes) using an image-pull secret or similar.

To create the Kubernetes cluster, run the following command:

`aksName=reviewsAksDemo`

`az aks create --resource-group $group --name $aksName --node-count 1 --generate-ssh-keys --network-plugin azure`

Note that I have created a single node cluster to save costs. This is a single point of failure, and in reality you will want to provision multiple nodes. So we can pull images from ACR, we need to attach it to the cluster. This negates the need to setup an image-pull secret or similar:

`az aks update --resource-group $group --name $aksName --attach-acr $containerRegistryName`

You can get the details of your new cluster with:

`az aks list --output table`

We'll also want to grab the credentials to connect to and manage the cluster:

`az aks get-credentials -g $group -n $aksName`

You can view the cluster credentials using the `kubectl` command:

`kubectl config get-contexts`

The target context will have an asterisk next to it - make sure you are targeting the correct context or use `kubectl config use-context <context-name>` to switch.

## Install the KEDA metrics for storage queues

There are multiple options for [installing KEDA here](https://keda.sh/docs/1.4/deploy/), including HELM. One really useful option is to use the Functions command line tools. Thank-you to this [Azure blog post](https://medium.com/microsoftazure/lifting-function-to-kubernetes-with-keda-e24de86fca2e) for the tip:

`func kubernetes install --namespace keda`

This will install KEDA into the (new) namespace `keda` on the cluster. There details of the `func install` and `func deploy` commands under the [`func cli` documentation](https://github.com/Azure/azure-functions-core-tools#deploying-a-function-to-aks-using-acr).

## Deploy the service to Kubernetes

Next, we will need to create a Kubernetes deployment object as a YAML manifest. To do so use the `func kubernetes deploy` command with the `--dry-run` option and pipe out the deployment YAML to a file:

`mkdir ./manifests`
`func kubernetes deploy --name review-functions --image-name "$reviewsDockerImageName" --dry-run > ./manifests/review-function-deploy.yaml`

Note that you can get detailed documentation using the `-h` flag, e.g. ` func kubernetes deploy -h`.

Take a moment to review the contents of `./manifests/review-function-deploy.yaml` you will find several objects being declared:

- a secret for the connection string. In my case these were derived from the `local.settings.json` and base64 encoded
    - I deleted all of the Azure resources after writing this tutorial - so these credentials will no longer work - but be careful not to commit these secrets to source control
- a secret for the Azure Function keys
- several service and role related objects
- a load-balancer service
- deployments objects for our functions. Interestingly, there was a deployment for each function, which makes sense as they use different scaling metrics
- an `azure-queue` KEDA `ScaledObject`

You may wish to tweak the ports, resource limits and probably the names of the deployments. As an aside, you may be wondering what happens if we add another queue triggered function. Does the deployment YAML include a third deployment object? I tried this out with the same connection-string - but with a different queue - and it turns out that a second function gets added to the `review-functions` deployment. A snippet of the deployment object - which includes a reference to the `ReviewsQueue2` trigger - can be found below:

```
spec:
  selector:
    matchLabels:
      app: review-functions
  template:
    metadata:
      labels:
        app: review-functions
    spec:
      containers:
      - name: review-functions
        image: reviewscontainerregistrydemo.azurecr.io/reviews-processor:latest
        env:
        - name: AzureFunctionsJobHost__functions__0
          value: ReviewQueue2
        - name: AzureFunctionsJobHost__functions__1
          value: ReviewQueueListener
```

If you wanted more control over this behaviour, it appears you can tell the Azure Functions Job Host which functions using one or more `AzureFunctionsJobHost__functions__<array-index>` environment variables, where `<array-index>` can be replaced with the array index of each function listed. See the [host.json reference for functions](https://docs.microsoft.com/en-us/azure/azure-functions/functions-host-json#functions) for context. If you wanted a separate deployment for this function, you could add a new deployment object, and use this variable to indicate which function(s) should be run for the container.

After reverting to the source code in this tutorial - i.e. without `ReviewsQueue2` - we'll go ahead and deploy the defaults with the following `kubectl` command:

`kubectl apply -f ./manifests/review-function-deploy.yaml`

Note that the completion of this command doesn't guarantee completion of our deployment. To monitor that, we'll list the deployments for our cluster:

`kubectl get deployments`

Check on the status of an individual deployment with the describe command, e.g.

`kubectl describe deployment/review-functions`
`kubectl describe deployment/review-functions-http`

The `func deploy` command generated two deployment objects - `review-functions` for the queue triggered function and `review-functions-http` for the HTTP triggered function. When I reviewed the events under `kubectl describe deployment/review-functions`, I noted that a single `pod` was provisioned, and torn down shortly after due to the lack of messages.

I always like to look at the `pod` logs after a deployment. To list the pods, use:

`kubectl get pods`

You can then get the logs via the `pod` name, e.g.:

`kubectl logs pod/review-functions-http-7bd44b6df4-srh8m`

However, I prefer to watch the logs by the app label so that I can watch new pods coming online:

`kubectl logs -l app=review-functions-http --follow --tail 1000`

I could see from the logs that the `ReviewGenerator` HTTP function came online using (container) port `80` with the route `api/review`:

```
info: Microsoft.Azure.WebJobs.Script.WebHost.WebScriptHostHttpRoutesManager[0]
      Initializing function HTTP routes
      Mapped function route 'api/review' [get,post] to 'ReviewGenerator'

info: Host.Startup[412]
      Host initialized (113ms)
info: Host.Startup[413]
      Host started (121ms)
info: Host.Startup[0]
      Job host started
Hosting environment: Production
Content root path: /
Now listening on: http://[::]:80
```

## Test KEDA auto-scaling

The easiest way to test KEDA queue based auto-scaling is to put a bunch of messages in the queue. To do so, we can call our `api/review` many times by hitting refresh in the browser - I have (lazily) allowed the `GET` verb to make this simpler.

Before we can call the HTTP function, we'll need to get the AKS cluster IP. List the running services like so:

```
$ kubectl get services
NAME                    TYPE           CLUSTER-IP    EXTERNAL-IP    PORT(S)        AGE
kubernetes              ClusterIP      10.0.0.1      <none>         443/TCP        132m
review-functions-http   LoadBalancer   10.0.42.152   20.53.178.74   80:32647/TCP   9m4s
```

In my case the `review-functions-http` service was provisioned an external IP of `20.53.178.74` and exposed on port `80`.  The IP address will be unique to your cluster when you run this. Before calling the endpoint, let's watch the pods with:

`kubectl get pods --watch`

Initially, you should see a HTTP function pod for `review-functions-http-<random-string>`. Let's prompt KEDA to provision some queue listener pod instances (starting with `review-functions`) by adding messages to the queue and monitoring this by watching the output of our `get pods` command.

Noting the external IP address, I called:

`http://20.53.178.74/api/review`

...and voila - I received a response for a new (random bogus) review:

```
{
    "EventId": "c17e2559-cd48-4d8d-9014-fc2ba572cdbd",
    "SubjectId": "8d7748a5-3066-42aa-b5cd-6ef74b83c1a7",
    "EventType": "ReviewSubmitted",
    "Content": {
        "Text": "This product works very well. It romantically improves my football by a lot."
    }
}
```

After having a good laugh, I did this several more times. After a short delay, I started to see `review-functions` (queue-trigger) pods start to appear - and eventually terminate - as shown below:

```
$ kubectl get pods --watch
NAME                                     READY   STATUS    RESTARTS   AGE
review-functions-7f788956bb-5898s        1/1     Running   0          3m38s
review-functions-7f788956bb-797pf        1/1     Running   0          3m38s
review-functions-7f788956bb-7ssqp        1/1     Running   0          5m51s
review-functions-7f788956bb-h2vm9        1/1     Running   0          3m54s
review-functions-7f788956bb-lblkc        1/1     Running   0          3m38s
review-functions-7f788956bb-mwqw6        1/1     Running   0          3m54s
review-functions-7f788956bb-nz9z4        1/1     Running   0          3m38s
review-functions-7f788956bb-tjwxd        1/1     Running   0          3m54s
review-functions-http-7bd44b6df4-6xvzw   1/1     Running   0          19m
review-functions-7f788956bb-797pf        1/1     Terminating   0          4m51s
review-functions-7f788956bb-lblkc        1/1     Terminating   0          4m51s
review-functions-7f788956bb-5898s        1/1     Terminating   0          4m51s
review-functions-7f788956bb-tjwxd        1/1     Terminating   0          5m7s

```

Here we are seeing the KEDA queue-based auto-scaler detecting the arrival of many queue messages, and scaling out the queue-listener pods to handle the load. Once KEDA detected that the rate of new messages had decreased, these pods started terminating as part of the scale down. Pretty cool!

## Cleanup

If you create an AKS cluster, note that this will create one or more Virtual Machines. The cost of leaving this running can add up if you leave the cluster running, so you will likely want to cleanup by deleting the resource group (and therefore its contents). To do so, run:

`az group delete -n $group`

You might want to also consider adding billing alerts against your subscriptions. I find the following command useful for viewing consumption and costs, or you can also use the Azure portal:

`az consumption usage list --output tsv > ./consumption.tsv`

You can then open and sort `./consumption.tsv` as a spreadsheet. I spent about `1.93 AUD` to write this tutorial.

Thank you for reading - I hope your enjoyed the tutorial!