# 🚀 PatientFlow Platform: Cloud-Native Microservices with AI & Kafka for Healthcare Ops

## 📌 Overview

**PatientFlow Platform** is an **AI-powered cloud-native, microservices-based healthcare backend system** designed to manage patient operations **efficiently, securely, and scalably**.

The platform demonstrates **real-world production architecture patterns**, including:
- **Microservices architecture** with independent, scalable services
- **Event-driven communication** via Kafka for asynchronous workflows
- **API Gateway pattern** for centralized routing, aggregation, and rate-limiting
- **Observability & monitoring** with Prometheus and Grafana
- **CI/CD automation** for consistent, production-ready deployments
- **Containerization & Orchestration** with Docker and Kubernetes
- **Automated testing** for critical services like Auth and Patient management to ensure reliability
- **AI-powered LLM integration with RAG** for intelligent, context-aware responses: the system can retrieve relevant **patient data on-demand** and generate **accurate answers**, combining **database context and real-time retrieval from Redis**

It simulates how **modern healthcare platforms** handle authentication, patient data, billing, and system communication **at scale**, enhanced with **robust testing, AI, and retrieval-augmented intelligence**.   


## 🏗️ Architecture

<p align="center">
  <img width="800" alt="PatientFlow Platform — high-level architecture diagram showing API Gateway, Auth, Patient, Billing, and AI microservices connected via Kafka, gRPC, Postgres, and Redis" src="https://github.com/user-attachments/assets/0b57071c-c1ef-454b-b869-2490c2be0bc1" />
</p>


## 🔧 Core Components

### 1. API Gateway
- Central entry point for all client requests
- Handles routing, aggregation, rate limiting, and health checks

### 2. Microservices
- **Auth Service** → Authentication & authorization, with **automated tests** ensuring robust security and reliability
- **Patient Service** → Patient data management, with **unit & integration tests** for data consistency
- **Billing Service** → Billing and transactions, event-driven processing
- **AI Service / Chatbot** → Handles **LLM-based responses** with **RAG functionality**, combining retrieved patient context from **database and Redis cache for intelligent answers**   

### 3. Communication Layer
- **gRPC** → Fast synchronous communication between services  
- **Kafka** → Event-driven asynchronous messaging  

### 4. Data Layer
- **PostgreSQL** → Persistent storage  
- **Redis** → Caching and fast patient context access, **enabling RAG for AI queries**
- **RAG Layer** → Dynamically retrieves relevant patient information from database/cache for **context-aware LLM responses**

### 5. Observability
- **Prometheus** → Metrics collection  
- **Grafana** → Visualization dashboards  

### 6. DevOps & Deployment
- **Docker** → Containerization  
- **GitHub Actions (CI/CD)** → Automated build & push  
- **Kubernetes (Manifests)** → Orchestration-ready setup  


## ⚙️ System Functionality

The platform simulates a **real-world healthcare workflow**, now enhanced with **robust testing, AI, and RAG capabilities**:

1. User authentication using **JWT-based RBAC** via **Auth Service**    
2. **Patient Service:** Patient data creation with **automated tests**, **publishing events to Kafka** for downstream processing  
3. **Billing operations (event-driven):**  
- Billing Service **listens to Patient Created events** from Kafka    
- Automatically **creates a billing account** for the patient  
- Publishes **billing events to Kafka**, which are then used to **update Redis for AI context**  
4. Service communication:
- **Synchronous → gRPC**
- **Asynchronous → Kafka**
5. **AI-Powered Chatbot (LLM + RAG):**   
- Uses **LLaMA 3 integration** to answer patient-related queries  
- **Retrieval-Augmented Generation (RAG):** fetches **up-to-date patient and billing context from Redis and database**  
- Provides **context-aware, accurate responses** reflecting the latest data  
6. **API Gateway:**   
- Routes all external requests
- Applies rate limiting
7. **Health checks** ensure service availability  
8. **Metrics** are monitored in real-time via **Prometheus and Grafana**


## 🔄 CI/CD Pipeline

The project includes a fully automated pipeline:

### 1. Workflow
- Code pushed to repository  
- GitHub Actions triggers pipeline  
- Docker images built for each service  
- Images pushed to Docker Hub  

### 2. Outcome
- Ready-to-deploy container images  
- Consistent and automated builds  


## ☸️ Kubernetes Support

Kubernetes manifests are included for all services.

⚠️ **Note:**
- Deployment may fail on low-resource systems (e.g., Minikube) due to memory limits  
- Recommended to use a cloud Kubernetes cluster for full setup  


## 🛠️ Steps for Implementation

1. Clone Repository:   
**git clone** https://github.com/Aishwarya-K-R/PatientFlow-Platform.git   
**cd PatientFlow-Platform**   
2. Create **.env** file in the project root and provide the data required for **docker-compose.yml** file  
3. For Kubernetes setup, provide the required data in **secrets.yml and config-map.yml** files   
4. Run Automated Tests: **dotnet test PMS.Tests/PMS.Tests.csproj**
5. Serve the model and pull it locally:  
**ollama serve   
ollama pull llama3**  
6. Run with Docker Compose: **docker-compose up --build**  
7. Access the services:  
- **API Gateway:** http://localhost:4004/   
- **Health Check:** http://localhost:4004/health  
- **Kafka UI:** http://localhost:8080  
- **Prometheus:** http://localhost:9090     
- **Grafana:** http://localhost:3000   
- **AI-Powered Chatbot:** http://localhost:4004/ai/ask   
8. Run Kubernetes and accesss the services using domain name (based on the setup): **kubectl apply -f Kubernetes/**
  

## 📊 Event Streaming via Apache Kafka

### 🟢 Patient Service: Performs CRUD operations on patients and publishes events to Kafka   

<p align="center">
  <img width="800" alt="Patient Service — Kafka UI screenshot showing patient-created, patient-updated, and patient-deleted topic events" src="https://github.com/user-attachments/assets/f73bba52-fef0-4efa-857f-7ef2410a28ec" />
</p>

### 🔵 Billing Service: Consumes patient create events, creates billing accounts, and publishes billing events to Kafka  

<p align="center">
  <img width="800" alt="Billing Service — Kafka UI screenshot showing billing-created events published in response to patient-created events" src="https://github.com/user-attachments/assets/60e5041c-5839-439e-b83e-db15a614e457" />
</p>

### 🟢 AI Service: Consumes patient and billing events to update Redis cache for RAG-powered LLM responses  

<p align="center">
  <img width="800" alt="AI Service — Kafka UI screenshot showing the AI service consuming patient and billing topics to refresh Redis context for the LLM" src="https://github.com/user-attachments/assets/27324373-23ab-4366-a565-1e8eebbb57f3" />
</p>


## 📊 Monitoring & Observability Stack  

### 🔵 Prometheus: Collects and aggregates real-time metrics across microservices for deep system observability  

<p align="center">
  <img width="800" alt="Prometheus targets dashboard — all microservices (auth, patient, billing, AI, gateway) reporting UP" src="https://github.com/user-attachments/assets/cc54c279-6d43-42c8-af33-5c21153f7fb4" />
</p>

<p align="center">
  <img width="800" alt="Prometheus metrics query view — sample query showing HTTP request metrics across services" src="https://github.com/user-attachments/assets/519d36f9-1c5a-4d4d-95eb-bf52d752b907" />
</p>

### 🟢 Grafana: Transforms metrics into powerful visual dashboards for real-time insights and performance monitoring

<p align="center">
  <img width="800" alt="Grafana dashboard — PatientFlow microservices metrics including request rate, latency, and error rate panels" src="https://github.com/user-attachments/assets/2c53866d-f42f-4b51-8399-1974b614ce09" />
</p>
