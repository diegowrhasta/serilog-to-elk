# Serilog to Elk

Prototype to see how to integrate Serilog with the ELK (Elasticsearch, Logstash, Kibana)
stack for logging storage plus processing and visualizing.

A good bootstrapper is [docker-elk](https://github.com/deviantony/docker-elk). This 
setup was used as inspiration for the one we have under `compose.yml`, however, due 
to its more extensible and complex nature, a lot of its features were also ignored. 
However, it's a great starting point for a robust, and perhaps production-ready 
architecture.

The way to run the `docker-compose` file should be like: `docker-compose up --build -d`. 
We want the `--build` flag due to the fact that we want to always rebuild the API 
image, in case we make editions and want to put that new build into the orchestrated 
environment.

_Give it a minute so that everything boots up correctly_. Start hitting the REST 
endpoints and generate logs. You can then go into Kibana and create indexes, and 
visualize their info.

## Setup

A simple `docker-compose up -d` at the root of the solution should be enough to 
get this up and running. The ELK stack consists of three main components

**Elasticsearch:** A distributed, open-source search and analytics engine designed 
for handling large volumes of data. Built on Apache Lucene, provides a scalable, 
real-time search solution with a RESTful API.
```
  elasticsearch:
    image: docker.elastic.co/elasticsearch/elasticsearch:8.5.0
    container_name: elasticsearch
    environment:
      - discovery.type=single-node
      - ES_JAVA_OPTS=-Xms512m -Xmx512m
      - xpack.security.enabled=false # disable by default HTTPs
      - xpack.license.self_generated.type=basic
      - xpack.monitoring.collection.enabled=false
      - xpack.watcher.enabled=false
      - xpack.ml.enabled=false
      - xpack.security.enrollment.enabled=false
    ports:
      - "9200:9200"
    networks:
      - elk
```
We are running Elasticsearch not on https, with paid features disabled, it also 
limits Java heap size to **512MB** reducing memory usage. It also exposes port 
`9200`, allowing external access to the REST API, by the end it connects to the `elk` 
network.

**Logstash:** Is an open-source data processing pipeline that ingests, transforms, 
and sends data to various destinations. It's widely used for log and event data 
processing.
```
  logstash:
    image: docker.elastic.co/logstash/logstash:8.5.0
    container_name: logstash
    volumes:
      - ./logstash.conf:/usr/share/logstash/pipeline/logstash.conf
    depends_on:
      - elasticsearch
    ports:
      - "5044:5044" # Beats input
      - "5000:5000/tcp" # TCP input
      - "5000:5000/udp" # UDP input
      - "9600:9600" # Monitoring API
    networks:
      - elk
```
We are mounting our local `logstash.conf` into the container's `/usr/share/logstash/pipeline/logstash.conf` 
path, this is key since it will dictate how logstash will behave. It also has a 
dependency to `elasticsearch` in order to ensure that this service starts after it, 
and we are exposing different _standard_ ports so that we can receive info and also 
expose some other entry points to the outside.

And also in order for logstash to work properly we need a `logstash.conf` file at
the same level as our docker-compose file.

For the full description of what the config file does, head down to 
**Aggregating log data**

We are configuring by default for logstash to be listening for incoming logs
from **Beats** (such as Filebeat or Metricbeat) on port **5044**, any log data
sent from a configured Beat will be received and processed by Logstash.

This basic configuration can be expanded further with:

- Tons of filtering and transformations.
- Additional input sources (e.g., TCP, UDP, Kafka).
- Authentication/security settings. (Make it https)

**Kibana:** Open-source data visualization and exploration tool designed for analyzing 
and visualizing data stored in _Elasticsearch_. Widely used for log analysis, monitoring, 
and business intelligence.
```
  kibana:
    image: docker.elastic.co/kibana/kibana:8.5.0
    container_name: kibana
    environment:
      - ELASTICSEARCH_HOSTS=http://elasticsearch:9200
    depends_on:
      - elasticsearch
    ports:
      - "5601:5601"
    networks:
      - elk
```
We are using an env variable to point to the `elasticsearch` service, we are making 
sure kibana starts after this same service, and we expose `5601` so that its 
web page is exposed.

**Network**

Since this works as a cluster (different containers for different services), we 
need for them to be inside a private network so that they can talk between each 
other.
```
networks:
  elk:
    driver: bridge
```
Under a network named `elk` we will have our containers communicating.

**Filebeat:** Lightweight shipper for forwarding and centralizing log data. Installed 
as an agent on a server, it monitors the log files or locations that you specify, 
collects log events, and forwards them either to _Elasticsearch_ or _Logstash_
```
  filebeat:
    image: docker.elastic.co/beats/filebeat:8.5.0
    container_name: filebeat
    user: root  # Filebeat needs root access to read logs
    depends_on:
      - elasticsearch
      - serilogtoelk.api
    volumes:
      - ./logs:/logs:ro  # Read logs from shared volume
      - ./filebeat.yml:/tmp/filebeat.yml:ro
      - ./filebeat-entrypoint.sh:/filebeat-entrypoint.sh:ro
    entrypoint: /filebeat-entrypoint.sh
    networks:
      - elk
```

In this project we are leveraging the root of the repository to save different files and folders 
to then reference them across our different containers. It's **highly advisable** 
to run everything with `docker-compose`, everything will fit into place. When 
we run it, the way things interconnect is as follows:

- The Web API will have all its logs mounted to a `logs/` folder next to the 
`compose.yml` file, it's from that same path that **filebeat** will start reading
logs by mounting them to its internal `/logs` path. When mounting volumes you can 
add a flag `:ro` so that nothing can be changed from the container's perspective.
- The `filebeat.yml` file that we also have at the root of the repo will be mounted 
to a temporary path inside the container `/tmp/filebeat.yml:ro` it will also be 
read-only.
- _Specially_ if you are working on Windows, file permissions just go out of whack, 
and they end up open for everything and everyone. **_filebeat doesn't like that_**. 
Hence, we have a `filebeat-entrypoint.sh` file also at the root of the repo, and it's 
also mounted to the container and marked as the entrypoint for the container, so 
that the moment the container boots up it will execute said script.

The script does the following:
```
#!/bin/sh
# Copy the filebeat.yml file from a temporary location to the desired location
cp /tmp/filebeat.yml /usr/share/filebeat/filebeat.yml

# Change the permissions of the file
chmod 644 /usr/share/filebeat/filebeat.yml

# Start Filebeat
exec filebeat -e --strict.perms=false
```
We make a copy inside the container of that read-only file we mounted from our 
repo path. We then change the permissions there so that only the owner can read, 
write, execute it. And lastly we execute `filebeat`. It's only by doing this that 
filebeat won't complain about the file having too-permisive settigs on the `.yml`.

**Extra comment:** There's also a flag to run filebeat's _setup_ which based on 
a `setup.kibana` configuration, it will try to connect both to Kibana and to 
Elasticsearch and run pre-configurations. For our particular use case, it's redundant, 
and we won't use that. The docs sure point to that as a first step, if you want 
to see how that looks and works, check the **References** section by the end.

_NOTE:_ All the paths to where we mount to, copy to are intentional since the tools 
by convention will look at those paths for configuration files.

Further details in regard to its config file will be at the **Aggregating log data** section, 
but simply put, we are configuring `filebeat` to be listening for logs on a specified path, 
and then stating how we then emit them to a `logstash` instance, there's a lot of 
other options to put here (directly emit to Elasticsearch, add extra fields or 
manipulate the data a bit before sending it to an _output_).

## ELK Notes

- Starting from version 8 of the stack, https is enabled by default, hence if trying 
to get it up and running without specific flags to make it `http` it will fail on 
its connection. `xpack.security.enabled=false`. (And it's widely mentioned that 
the jump from v7 to v8 is massive).
- There's also a licensed version of the stack and a free-open-source one. Again, 
by default it will try to make use of the license, we can turn that off 
in case we don't want to use up our license, nor will we need those features:

```
- xpack.license.self_generated.type=basic
- xpack.monitoring.collection.enabled=false
- xpack.watcher.enabled=false
- xpack.ml.enabled=false
- xpack.security.enrollment.enabled=false
```
| Configuration                                      | Description                                                                 |
|-----------------------------------------------------|-----------------------------------------------------------------------------|
| `xpack.license.self_generated.type=basic`           | Ensures only free features are enabled.                                     |
| `xpack.security.enabled=false`                      | Disables authentication and TLS.                                            |
| `xpack.monitoring.collection.enabled=false`         | Disables monitoring (some features require a paid license).                 |
| `xpack.watcher.enabled=false`                       | Disables the Watcher (alerting system, paid feature).                       |
| `xpack.ml.enabled=false`                            | Disables Machine Learning (paid feature).                                   |
| `xpack.security.enrollment.enabled=false`           | Disables security auto-enrollment (paid feature).                           |

_Note:_ The X-Pack Monitoring feature in Elasticsearch collects and visualizes 
metrics about ElasticSearch, Logstash and Kibana instances, including:

- Cluster Health (e.g., node status, memory, CPU usage)
- Index Statistics (e.g., document count, indexing rate)
- Search Performance (e.g., query latency)
- Logstash Pipeline Metrics (if enabled)

The data collection part is _free_, however the **Kibana UI for monitoring** is 
a **paid feature**. We would lose that plus some advanced built-in dashboards.

If this is disabled we will still have access to things such as:

- Basic cluster health via:

```
curl -X GET "http://localhost:9200/_cluster/health?pretty"
```

- Index and node stats via:

```
curl -X GET "http://localhost:9200/_cat/indices?v"
curl -X GET "http://localhost:9200/_cat/nodes?v"
```

Plus Logs from Elasticsearch and Logstash containers (`docker logs`).

If we don't need a built-in UI for monitoring, disabling it saves memory and CPU, 
if we need some metrics we can still make **manual API calls** to Elasticsearch. 
In case we need some of those advanced monitoring features we can use open-source 
alternatives like **Prometheus + Grafana**.

- For production ready environments, we should put walls up in the form of 
credentials such as users to log-in, to connect, and others. By leveraging a 
`.env` file we can easily inject those credentials and not hard-code them.
- For security, enabling SSL is also a must
- We can increase the `ES_JAVA_OPTS` if handling large logs

## Connecting Serilog to ELK

First of all, there used to be a Serilog maintained sink: `Serilog.Sinks.Elasticsearch`, 
however it has been marked as deprecated for `Elastic.Serilog.Sinks` instead.

Besides that, so that we work in a performant manner and with extra metadata that 
might be useful we also have installed:

- Serilog.Enrichers.Environment
- Serilog.Enrichers.Thread
- Serilog.Sinks.Async
- Serilog.Formatting.Compact.CompactJsonFormatter

We should also note that by installing specifically `Serilog.AspNetCore`, lots of 
other transitive packages get added, and it's through them that we get functionality 
such as the extension method to both read from the config and add Serilog and so 
on: (E.g., `ReadFrom` comes specifically from `Serilog.Configuration`).

```csharp
builder.Host.UseSerilog((ctx, lc) =>
    lc.ReadFrom.Configuration(ctx.Configuration));
```

**_BIG NOTE:_** As per recommendation on the [Elasticsearch's team docs](https://www.elastic.co/guide/en/ecs-logging/dotnet/current/serilog-data-shipper.html), 
the newer implementation for the sink is way more lean, not as much configuration 
and things as the previously maintained package. It also adheres specifically to 
`ELK 8.x`. _It's also in the documentation recommended to make use of FileBeat_. 
Which is an extension to the _ELK_ ecosystem to read up on log files that are written 
to a file, and then streamed to _Logstash_ so that it then gets sent all the way 
to Elasticsearch. However, for the purposes of this DEMO we will have the two variations, 
depending on a flag on the config. [Filebeat Reference](https://www.elastic.co/beats/filebeat).

```
"WithFileBeats": true,
```

If this is set to `true`, we will only generate Console and File logs, if it's set 
to `false` it will try to emit to a `Elasticsearch` instance. Depending on its value 
under `ElasticSearch:ConnectionString` we could be connecting from an app running 
on the host or on a docker-compose cluster.

### Generating files

In order to integrate correctly with Elasticsearch, it's recommended by the docs 
to make use of the `EcsTextFormatter`. Something lives in a transitive package:

```
"Using": [
      "Elastic.Serilog.Sinks",
      "Elastic.CommonSchema.Serilog",
      "Serilog.Enrichers.Environment",
      "Serilog.Enrichers.Thread",
      "Serilog.Sinks.Async",
      "Serilog.Sinks.Console",
      "Serilog.Sinks.File"
    ],
//...//
"formatter": "Elastic.CommonSchema.Serilog.EcsTextFormatter, Elastic.CommonSchema.Serilog"
```

It's by using this formatter for our **File sink** that we will start saving the 
logs in a form that should be as easy to integrate and visualize in Kibana.

_Don't forget that you can override really nested objects by referring to indexes 
and object names: (E.g., `Serilog:WriteTo:1:Args:configure:0:Args:NodeUris`)._

For the record I will leave information that might be useful, one way 
or another from the old deprecated library.

The configuration for `Elasticsearch` would look something like this:

```
{
  "Name": "Elasticsearch",
  "Args": {
    "NodeUris": "http://elasticsearch:9200",
    "IndexFormat": "logs-dotnet-{0:yyyy.MM.dd}",
    "AutoRegisterTemplate": true,
    "AutoRegisterTemplateVersion": "ESv8",
    "BatchPostingLimit": 50,
    "QueueSizeLimit": 1000,
    "EmitEventFailure": "WriteToSelfLog",
    "FailureCallback": "console",
    "OverwriteTemplate": true
  }
}
```

| **Parameter**              | **Description**                                                                                                                                                                                                 |
|----------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **NodeUris**               | The address of the Elasticsearch server that will receive the logs. Since the .NET service is in the same Docker network (elk), it can resolve `http://elasticsearch:9200`.                                      |
| **IndexFormat**            | Defines the index name format where logs will be stored. Here, logs are stored in daily indices like `logs-dotnet-2025.02.24`. The `{0:yyyy.MM.dd}` part ensures daily index rotation.                          |
| **AutoRegisterTemplate**   | Registers an index template in Elasticsearch to structure incoming logs properly. This ensures Elasticsearch knows how to store log fields (e.g., timestamps, messages, levels).                                |
| **AutoRegisterTemplateVersion** | Specifies the Elasticsearch version compatibility (ESv8 ensures compatibility with Elasticsearch 8.x).                                                                                                   |
| **BatchPostingLimit**      | Limits the number of log events sent in one batch to 50, reducing the load on Elasticsearch.                                                                                                                   |
| **QueueSizeLimit**         | Defines the maximum number of log events held in memory before being dropped if they can't be sent. This prevents memory overflow when Elasticsearch is unavailable.                                             |
| **EmitEventFailure**       | Defines how failures are handled when an event can't be logged. `"WriteToSelfLog"` means failures will be logged into Serilog's internal self-log for debugging.                                                |
| **FailureCallback**        | Specifies an alternative failure handler. `"console"` means that failed log events will be printed to the application console.                                                                                  |
| **OverwriteTemplate**      | If `true`, Serilog overwrites existing Elasticsearch templates when auto-registering them, ensuring updates to log structures are applied.                                                                     |

**Notes:**

- Elasticsearch saves all of its data under indexes, with `IndexFormat` we are simply 
configuring the name of said indeces, which are purely _logical_. All indexed data 
gets saved under its internal data directories `/usr/share/elasticsearch/data`. 
Whether we have storaged configured and with physical persistent volumes we can 
keep this data.

### Templates

Elasticsearch **templates** define _how an index should be structured_ before data 
is stored. They act like **schemas** in a database, ensuring that fields are 
correctly mapped. (e.g, timestamps are dates, log levels are keywords).

When Serilog sends logs to Elasticsearch, it can **automatically create indices** 
based on `IndexFormat`. If no template exists, Elasticsearch may **guess** field 
types, leading to inconsistencies.

Three of the configurations we have laid out take care of controlling how Serilog 
ensures that logs are indexed with a correct and optimized structure. (`AutoRegisterTemplate`, 
`AutoRegisterTemplateVersion`, and `OverwriteTemplate`).

**_AutoRegisterTemplate:_** When enabled, **Serilog will automatically send an index template** 
to Elasticsearch before logging, this ensures fields (e.g., `@timestamp`, `level`, `message`) 
are **stored with the correct data types**. _Why would we want this?_ Without a 
predefined template, Elasticsearch **dynamically maps fields,** which can lead to 
_Fields being mapped incorrectly_ (e.g., timestamps stored as text); We could run 
into **index fragmentation** (e.g., `message` could end up being treated as a keyword 
instead of a full-text searchable field).

Template example:

```
{
  "index_patterns": ["logs-dotnet-*"],
  "settings": {
    "number_of_shards": 1
  },
  "mappings": {
    "properties": {
      "@timestamp": { "type": "date" },
      "level": { "type": "keyword" },
      "message": { "type": "text" },
      "Application": { "type": "keyword" }
    }
  }
}
```
When the first log is sent, Serilog ensures that Elasticsearch has a **template** 
like this, meaning that every new index (in our case, daily), will follow **this 
structure.**

**_AutoRegisterTemplateVersion:_** This will ensure that auto-registered templates 
are compatible with **Elasticsearch 8.x**, since it could happen that outdated 
template formats from previous versions clash. We have to set this depending on 
the version of Elasticsearch we are using since template formats **change the moment 
a new version drops**, depending on what version we use, Serilog will behave differently 
to adhere to the best practices of that specific version.

**_OverwriteTemplate:_** If a template with the same name exists, this will force 
**Serilog to overwrite it**, this will ensure that updates to **field mappings take 
effect immediately.** If we don't enable this, Elasticsearch **will not update an 
existing template**, so you might end up with outdated mappings (e.g., a field that 
was previously a string but is now a date), and so logs may end up indexed incorrectly 
since they are away from what the new standard is, so if we add fields, remove them, 
or change the nature of one of them, we should update their template immediately.

**ON THE NEW ELASTICSEARCH VERSION:**

We configure the index name with `DataStreamName`:

```
opts.DataStream = new DataStreamName("logs", "console-example", "demo");
```

### Request logging

There's this extension method that is a marvel to know about, specially with ASP.NET 
applications integrated with Serilog:

```
app.UseSerilogRequestLogging();
```

Taken from its own summary:

_Adds middleware for streamlined request logging. Instead of writing HTTP request 
information like method, path, timing, status code and exception details in several 
events, this middleware collects information during the request (including from 
IDiagnosticContext), and writes a single event at request completion. Add this in 
Startup. cs before any handlers whose activities should be logged._

This is **key** to get good metrics that are insightful when diagnosing and analyzing 
the health of a web app.

## Aggregating log data

_HINT_: There's tons of misinformation and deprecated knowledge out there, the 
documentation also is quite insipid at times. However, there's tons of content in 
the form of YouTube videos, GitHub repositories and other resources that should help 
you steer into the right direction as to how leverage the whole stack and get the 
results you want. This is just my own interpretation of it, but, _from the looks 
of it,_ it seems as though Elastic had many of its tools in charge of functions 
that other tools are specialized in, and so in an effort of separating concerns 
many options were taken out, and enriched in their respective application.

The way to understand concepts should start from how the log data flows into becoming 
information:

Web App => Log to File => Filebeat reads file => Emits to Logstash => Logstash transforms => Emits to Elasticsearch => Kibana visualizes data

- Our Web App, is configured to log into a file. This file will hold logs in a JSON 
format courtesy of the `EcsFormatter` that Elasticsearch provides to us.
- Filebeat will be constantly reading the logs folder and the moment it picks up on 
new entries it will emit them _RAW_ to Logstash through the network. This is its
configuration:
```
filebeat.config:
  modules:
    path: ${path.config}/modules.d/*.yml
    reload.enabled: false

filebeat.inputs:
  - type: log
    enabled: true
    paths:
      - "/logs/*.log"

setup:
  kibana:
    host: 'kibana:5601'
  template:
    overwrite: true
#filebeat.autodiscover:
#  providers:
#    - type: docker
#      hints.enabled: true

processors:
  - add_cloud_metadata: ~
  - add_host_metadata: ~
#  - add_tags:
#      tags: [SerilogToElk.API]
#      target: 'ServiceLogAppName'

#output.elasticsearch:
#  hosts: '${ELASTICSEARCH_HOSTS:elasticsearch:9200}'

output.logstash:
  hosts: ["logstash:5044"]

```
- Filebeats will be listening to the container's `/logs/*log` pattern. We mount 
the API logs there, and have them suffixed with `.log` so that only those types 
of files are picked up on.
- Commented out we have different options that for the right use case can be 
extremely helpful. Such as pre-adding other fields (add_tags), or connecting 
directly to Elasticsearch (outout.elasticsearch). If we try to send things directly, 
we won't get the entries indexed correctly, however. 

- **Logstash** takes care of processing the logs so that they can be indexed later:
```
input {
  beats {
    type => "logs"
    port => "5044"
  }
}

filter {
  json {
    source => "message"
    target => "log"
  }
  
  if [log][log.level] {
    mutate {
        add_field => { "@level" => "%{[log][log.level]}" }
    }
  } else {
    mutate {
        add_field => { "@level" => "Information" }
    }
  }
}

output {
  if "Error" == [@level] {
    elasticsearch {
        hosts => ["elasticsearch:9200"]
        index => "error_logs"
        ssl => false
    }
  }
  else if "SerilogToElk.API.docker" in [log][labels][Application] {
    elasticsearch {
        hosts => ["elasticsearch:9200"]
        index => "serilog.logfile.webapi"
        ssl => false
    }
  }
  else {
    elasticsearch {
        hosts => ["http://elasticsearch:9200"]
        index => "other.log"
        ssl => false
    }
  }
  stdout { codec => rubydebug }
}
```
We will now describe what this whole config does.

- Thing of this as a pipeline we are configuring from the source from where the 
data will come from all the way to the output.
- We firstly configure the service to be listening on port `5044` to logs that will 
be incoming.
- We then establish a `filter`. As we mentioned already, we are saving data in a 
`JSON` format, and it's when arriving at `logstash` that the whole json string will 
be under a `message` field, we will parse that whole json structure (and index it 
in memory) to a new field called `log`.
  - It's by analyzing the actual logs in the file that we get an idea as to how to 
  navigate them.
  - At a first level we will have a `log.level` field, we then add new field 
  called `@level` to the resulting structure, this will simply map that same value 
  of `log.level` and in case a level is not present we will simply mark it as `Information`
- It's then that we introduce now the idea of indexes and how we could separate them 
based on different criteria
  - In our case, in case we pick up on a record that is an `Error` we immediately 
  send it to an `error_logs` index at Elasticsearch.
  - In case we detect under a nested property from the resulting log the name of 
  an app we ourselves want to track e.g., `SerilogToElk.API.docker`. We will send 
  all of those logs to another index called `serilog.logfile.webapi`.
  - As a fallback, in case none of those conditions match, we have a `other.log` 
  index to which all those extra logs will be sent to.

As you can see this is simply taking logs in a raw form, transforming them, cleaning 
them, hydrating with more info, and then discriminating where to send them on 
different **indexes** in Elasticsearch.

There's definitely a lot of things to take into consideration, since this is also 
part of **_Data Science_**, the idea of indexes being buckets of data that is grouped 
by specific criteria, and that they all have different types of fields, luckily 
the tools we used are intelligent enough to map things automatically and alleviate 
most of the grunt work for us. But in case there's a bad mapping, we have the 
choice to jump in and make the configuration manually. Still, we have to be aware 
that logs and their info should be decided **IN ADVANCE**, since if we already generated 
information, and we add new fields, it sometimes means we will have to re-index 
a ton of data, or create a new index for the new form with extra info that we 
needed and things can get ugly pretty quickly.

### Generating Dashboards from the data

_Starting from v8, we don't use the concept of indexes, but of Data Views_. **We need 
to create this index at the Kibana level so that we can visualize our logs.**

This is a series of **manual steps to create the index for our app**:

- Access Kibana:
  - Open the browser and navigate to http://localhost:5601.

- Navigate to Stack Management:
  - Click on the menu icon (☰) in the top-left corner.
  - Select "Stack Management" under the "Management" section.

- Create a Data View:
  - In the "Kibana" section, click on "Data Views".
  - Click the "Create data view" button.

- Define the Data View:
  - In the "Name" field, enter a name for the data view, such as `Serilog Logs`.
  - In the "Index pattern" field, input the pattern that matches the log indices. 
  For our Serilog setup, it should be `serilog.logfile.webapi` or `error_logs`.
  - Our logs include a timestamp field by default (thanks to Serilog), we can 
  select it to on the "Timestamp field" dropdown.
  - This enables time-based filtering on visualizations.

- Save the Data View:
  - Click the "Create data view" button to save.

And now, in order to visualize our logs, we can:

- Access Discover:
  - Click on the menu icon (☰) again.
  - Select "Discover" under the "Kibana" section.

- Select Your Data View:
  - In the top-left corner, there's a dropdown menu. Select the "Serilog Logs" 
  data view (or the name you assigned).

- Explore Your Logs:
  - Adjust the time filter in the top-right corner to the desired range.
  - Your logs should now be visible in the main panel.

## Extra Notes

```
"Properties": {
  "Application": "SerilogToElk.API"
}
```

When adding this property to Serilog's config, we can add global metadata that will 
be attached to every log entry. In this case, every log will have a `"Application": "SerilogToElk.API" 
extra key.

This can be later configured on the log processing pipeline so that this gets mapped 
to another field, and we enable further granular analysis (e.g., We can take a look 
at different applications if we are logging multiple ones, or filtering out by 
some machine-name or agent name).

## References

- [Send C# app logs to Elasticsearch via logstash and filebeat](https://www.youtube.com/watch?v=4ilUmga1A9w&t=626s) **Highly Recommended**
- [Elk with filebeat in docker-compose repository reference](https://github.com/gnokoheat/elk-with-filebeat-by-docker-compose)
- [Theory on Mapping](https://www.youtube.com/watch?v=MN5-AD5I8mI)
- [Theory on Custom Mappings](https://www.youtube.com/watch?v=PgMtklprDfc)
- [Serilog with Kibana](https://www.youtube.com/watch?v=0acSdHJfk64)
- [Great tutorial on filebeat setup and isolated containers](https://www.youtube.com/watch?v=kIkpR8bxey0)
- [Masterclass at debugging logstash plus filters](https://www.youtube.com/watch?v=_qgS1m6NTIE)
- [Deprecated Elasticsearch Sink Repository](https://github.com/serilog-contrib/serilog-sinks-elasticsearch)
- [Logstash Official Docs](https://www.elastic.co/guide/en/logstash/current/event-dependent-configuration.html)
- [ECS Logging i.e., How to structure your logs according to Elastic](https://www.elastic.co/guide/en/ecs-logging/overview/master/intro.html)
- [Filebeat Docs](https://www.elastic.co/guide/en/beats/filebeat/8.17/filebeat-overview.html)
- [Filebeat yml reference - Verbose](https://www.elastic.co/guide/en/beats/filebeat/8.17/filebeat-reference-yml.html)
- [Elastic's Serilog Formatter Nuget](https://www.nuget.org/packages/Elastic.CommonSchema.Serilog/)
- [Elastic's Maintained Serilog Sink](https://www.elastic.co/guide/en/ecs-logging/dotnet/current/serilog-data-shipper.html)
- [Elastic's Examples](https://github.com/elastic/ecs-dotnet/tree/main/examples/aspnetcore-with-serilog)
- [Serilog's Configuration Examples](https://github.com/serilog/serilog-settings-configuration/blob/dev/sample/Sample/appsettings.json)