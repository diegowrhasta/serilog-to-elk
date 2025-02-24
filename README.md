# Serilog to Elk

Prototype to see how to integrate Serilog with the ELK (Elasticsearch, Logstash, Kibana)
stack for logging storage plus processing and visualizing.

A good bootstrapper is [docker-elk](https://github.com/deviantony/docker-elk). This 
setup was used as inspiration for the one we have under `compose.yml`, however, due 
to its more extensible and complex nature, a lot of its features were also ignored. 
However, it's a great starting point for a robust, and perhaps production-ready 
architecture.

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
```
input {
  beats {
    port => 5044
  }
}

output {
  elasticsearch {
    hosts => ["http://elasticsearch:9200"]
    index => "logstash-%{+YYYY.MM.dd}"
    ssl => false
  }
  stdout { codec => rubydebug }
}

```

We are configuring by default for logstash to be listening for incoming logs
from **Beats** (such as Filebeat or Metricbeat) on port **5044**, any log data
sent from a configured Beat will be received and processed by Logstash.

After that we establish what logstash will output:

- It sends the processed log to Elasticsearch at `http://elasticsearch:9200`
- The index format will be `logstash-YYYY.MM.DD`, meaning the logs will be stored
  in daily indices (e.g., `logstash-2025.02.24`)
- It also has a second output which prints the log to the console, this is in
  **Ruby debug format** (pretty-printed json)

This basic configuration can be expanded further with:

- Filtering or transformation (i.e., Not passing logs directly to Elasticsearch as-is).
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
need for them to be inside of a private network so that they can talk between each 
other.
```
networks:
  elk:
    driver: bridge
```
Under a network named `elk` we will have our containers communicating.

## ELK Notes

- Starting from version 8 of the stack, https is enabled by default, hence if trying 
to get it up and running without specific flags to make it `http` it will fail on 
its connection. `xpack.security.enabled=false`.
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

- Index and node stats via::

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