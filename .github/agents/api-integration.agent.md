---
name: api-integration
description: "Azure Cost, Billing & FinOps API reference for agent tool development"
---

# Azure Cost, Billing & FinOps APIs

All ARM APIs use `https://management.azure.com/{scope}/providers/...` with Entra ID bearer tokens unless noted.

The agent itself is read-only and cannot call the `PUT`/`PATCH`/`DELETE` operations listed below — they are documented so generated remediation scripts use the correct endpoint, payload, and API version.

## 1. Cost Management (`Microsoft.CostManagement`)

- **Query** — POST `.../query` — aggregated cost analysis with grouping/filtering
- **Forecast** — POST `.../forecast` — projected costs from historical trends
- **Cost Details** — POST `.../generateCostDetailsReport` — async raw line-item cost report
- **Exports** — PUT `.../exports/{name}` — scheduled cost data export to Storage (CSV/Parquet/FOCUS)
- **Budgets** — PUT `.../budgets/{name}` — cost/usage budgets with threshold alerts
- **Alerts** — GET `.../alerts` — budget and anomaly alert notifications
- **Dimensions** — GET `.../dimensions` — available filter/grouping dimensions
- **Views** — PUT `.../views/{name}` — saved cost analysis views
- **Scheduled Actions** — PUT `.../scheduledActions/{name}` — recurring cost automation
- **Cost Allocation Rules** — PUT `.../costAllocationRules/{name}` — split shared costs across scopes
- **Settings** — GET/PUT `.../settings` — Cost Management feature configuration
- **Benefit Utilization Summaries** — GET `.../benefitUtilizationSummaries` — reservation + savings plan utilization
- **Benefit Recommendations** — GET `.../benefitRecommendations` — unified reservation + savings plan purchase recommendations
- **Reservation Details Report** — POST `.../generateReservationDetailsReport` — async reservation usage at billing scopes
- **Markup Rules** — PUT `.../markupRules/{name}` — partner/reseller markup pricing rules

## 2. Billing (`Microsoft.Billing`)

- **Billing Accounts** — GET `.../billingAccounts` — list/manage billing accounts (EA, MCA, CSP, MOSP)
- **Billing Profiles** — GET `.../billingProfiles` — invoice groupings (MCA)
- **Invoice Sections** — GET `.../invoiceSections` — charge organization within profiles
- **Invoices** — GET `.../invoices` — retrieve invoices, download PDFs
- **Transactions** — GET `.../transactions` — invoice line-items (charges, refunds, adjustments)
- **Billing Subscriptions** — GET `.../billingSubscriptions` — subscriptions linked to billing account
- **Billing Role Assignments** — GET/PUT `.../billingRoleAssignments` — billing RBAC
- **Billing Permissions** — GET `.../billingPermissions` — check billing access
- **Policies** — GET/PUT `.../policies` — billing policies (e.g., subscription creation)
- **Payment Methods** — GET `.../paymentMethods` — payment instruments
- **Available Balances** — GET `.../availableBalances` — credit/prepayment balances (MCA)
- **Agreements** — GET `.../agreements` — billing agreements (MCA, EA)
- **Products** — GET `.../products` — purchased products (SaaS, reservations)
- **Instructions** — GET `.../instructions` — EA billing instructions
- **Departments** — GET `.../departments` — EA department management
- **Enrollment Accounts** — GET `.../enrollmentAccounts` — EA enrollment account management
- **Customers** — GET `.../customers` — CSP customer management
- **Transfers** — POST `.../transfers` — billing ownership transfers
- **Associated Tenants** — GET `.../associatedTenants` — tenant associations
- **Billing Property** — GET `.../billingProperty` — billing property for a subscription
- **Billing Requests** — GET `.../billingRequests` — billing approval workflows

## 3. Consumption (`Microsoft.Consumption`) — Legacy, migrate to Cost Management

- **Usage Details** — GET `.../usageDetails` — line-item consumption (use Exports/Cost Details instead)
- **Marketplaces** — GET `.../marketplaces` — third-party Marketplace charges
- **Reservation Summaries** — GET `.../reservationSummaries` — daily/monthly reservation utilization
- **Reservation Details** — GET `.../reservationDetails` — per-instance reservation usage
- **Reservation Recommendations** — GET `.../reservationRecommendations` — purchase recommendations
- **Reservation Transactions** — GET `.../reservationTransactions` — purchase/refund/exchange history
- **Price Sheets** — GET `.../pricesheets` — negotiated/EA pricing for enrollment
- **Balances** — GET `.../balances` — EA monetary balance and overage
- **Charges** — GET `.../charges` — department/enrollment-level charges (EA)
- **Tags** — GET `.../tags` — cost allocation tags summary
- **Lots** — GET `.../lots` — prepayment/MACC lot balances
- **Credits** — GET `.../credits` — credit balance and usage
- **Events** — GET `.../events` — credit/prepayment transaction events

## 4. Reservations (`Microsoft.Capacity`)

- **Reservation Orders** — GET `.../reservationOrders` — list/manage reservation purchases
- **Reservations** — GET `.../reservations` — individual instances (scope, renewal, exchange)
- **Calculate Price** — POST `.../calculatePrice` — price a reservation before purchase
- **Purchase** — PUT `.../reservationOrders/{id}` — buy a reservation
- **Calculate Exchange** — POST `.../calculateExchange` — calculate exchange/return amounts
- **Return** — POST `.../return` — refund a reservation
- **Catalog** — GET `.../catalog` — available reservation SKUs

## 5. Savings Plans (`Microsoft.BillingBenefits`)

- **Savings Plan Orders** — GET `.../savingsPlanOrders` — list/manage savings plan purchases
- **Savings Plans** — GET `.../savingsPlans` — individual plan details and utilization
- **Calculate Price** — POST `.../calculatePrice` — price before purchase
- **Validate Purchase** — POST `.../validatePurchase` — check eligibility

## 6. Retail Prices (Public, No Auth)

- **Retail Prices** — GET `https://prices.azure.com/api/retail/prices` — public pay-as-you-go pricing with OData filters

## 7. Advisor (`Microsoft.Advisor`)

- **Recommendations** — GET `.../recommendations` — cost/performance/security recommendations (filter `Category eq 'Cost'`)
- **Suppressions** — PUT `.../suppressions/{name}` — dismiss/postpone recommendations
- **Configurations** — PUT `.../configurations` — tune thresholds (e.g., CPU % for right-sizing)

## 8. Resource Graph (`Microsoft.ResourceGraph`)

- **Resources** — POST `.../resources` — KQL queries across subscriptions (inventory, tags, SKUs, idle detection)

## 9. Azure Monitor, App Insights & Log Analytics — Workload Understanding

### 9a. Azure Monitor Metrics (`Microsoft.Insights`)

- **Metrics** — GET `.../metrics` — resource utilization metrics (CPU, memory, disk, network IOPS)
- **Metric Definitions** — GET `.../metricDefinitions` — available metrics per resource type
- **Metric Baselines** — GET `.../metricBaselines` — dynamic threshold baselines for anomaly detection
- **Metric Alerts** — PUT `.../metricAlerts/{name}` — alerts when utilization crosses thresholds
- **Activity Log** — GET `.../eventCategories` + `.../events` — resource lifecycle events (create/delete/modify)
- **Diagnostic Settings** — GET/PUT `.../diagnosticSettings` — configure what telemetry flows where
- **Autoscale Settings** — GET/PUT `.../autoscaleSettings` — autoscale rules (scale-based cost optimization)

### 9b. Application Insights (`Microsoft.Insights/components`)

- **Components** — GET `.../components` — list/manage Application Insights resources
- **App Insights Query** — POST `https://api.applicationinsights.io/v1/apps/{appId}/query` — KQL queries against App Insights telemetry (requests, dependencies, exceptions, performance)
- **Metrics** — GET `https://api.applicationinsights.io/v1/apps/{appId}/metrics/{metricId}` — pre-aggregated app metrics (request rate, response time, failure rate)
- **Events** — GET `https://api.applicationinsights.io/v1/apps/{appId}/events/{eventType}` — raw telemetry events (requests, traces, exceptions, dependencies)
- **Live Metrics** — streaming real-time request/response/failure rates
- **Usage & Estimated Costs** — GET `.../currentBillingFeatures` — ingestion volume and estimated cost of telemetry (meta-FinOps: cost of monitoring itself)

### 9c. Log Analytics Workspaces (`Microsoft.OperationalInsights`)

- **Workspaces** — GET `.../workspaces` — list/manage Log Analytics workspaces
- **Log Analytics Query** — POST `https://api.loganalytics.io/v1/workspaces/{workspaceId}/query` — KQL queries across all ingested logs (VM perf, container metrics, network, custom logs)
- **Workspace Usage** — GET `.../usages` — data ingestion volume per data type (optimize log costs)
- **Saved Searches** — GET/PUT `.../savedSearches` — reusable KQL queries
- **Tables** — GET `.../tables` — manage table schemas, retention, and ingestion plans (Analytics vs Basic vs Archive)
- **Data Export Rules** — PUT `.../dataExportRules` — continuous export to Storage/Event Hubs
- **Linked Storage Accounts** — PUT `.../linkedStorageAccounts` — customer-managed storage for logs

### 9d. Key FinOps Queries via Monitor/Log Analytics

These KQL tables are particularly FinOps-relevant:

- `Perf` — VM CPU, memory, disk counters (right-sizing)
- `InsightsMetrics` — VM Insights / Container Insights metrics
- `ContainerInventory` / `KubePodInventory` — AKS container resource requests vs actual usage
- `AzureMetrics` — platform metrics for PaaS services
- `AzureDiagnostics` — diagnostic logs (App Gateway, SQL, Firewall throughput)
- `Usage` / `_BilledSize` — Log Analytics ingestion volume (meta-cost: cost of observability)

## 10. Resource SKUs (`Microsoft.Compute`)

- **Resource SKUs** — GET `.../skus` — available VM sizes, capabilities, restrictions per region (right-sizing targets)

## 11. Resource Health (`Microsoft.ResourceHealth`)

- **Availability Statuses** — GET `.../availabilityStatuses` — resource health status for cost anomaly correlation
- **Events** — GET `.../events` — service health events (outages, maintenance)

## 12. Subscription Management (`Microsoft.Subscription`)

- **Subscriptions** — PUT `.../subscriptionDefinitions` — create/rename/cancel subscriptions
- **Subscription Operations** — GET `.../subscriptionOperations` — track async subscription operations

## 13. Quota Management (`Microsoft.Quota`)

- **Quotas** — GET `.../quotas` — quota limits and current usage per resource type/region
- **Quota Requests** — PUT `.../quotaRequests` — request quota increases

## 14. Carbon Optimization (`Microsoft.Carbon`) — Preview

- **Carbon Emission Reports** — POST `.../carbonEmissionReports` — emissions data by subscription/RG/resource

## 15. Azure Policy (`Microsoft.Authorization`)

- **Policy Definitions** — GET/PUT `.../policyDefinitions` — define rules (e.g., deny expensive SKUs, require tags)
- **Policy Assignments** — GET/PUT `.../policyAssignments` — assign policies to scopes (enforce tagging, restrict regions/SKUs)
- **Policy Set Definitions (Initiatives)** — GET/PUT `.../policySetDefinitions` — group related policies (e.g., "FinOps governance bundle")
- **Compliance Results** — GET `.../policyStates` — check compliance status (e.g., untagged resources, non-compliant SKUs)
- **Remediations** — PUT `.../remediations` — auto-remediate non-compliant resources (e.g., apply missing tags)

## 16. Management Groups (`Microsoft.Management`)

- **Management Groups** — GET/PUT `.../managementGroups` — create/list/manage org hierarchy for cost scoping
- **Descendants** — GET `.../descendants` — list subscriptions/child groups under a management group
- **Entities** — POST `.../getEntities` — discover all management groups + subscriptions the caller can see

## 17. Tags (`Microsoft.Resources`)

- **Tags** — GET/PUT `.../tagNames` — create/list tag keys at subscription level
- **Tag Values** — GET/PUT `.../tagNames/{tagName}/tagValues` — create/list tag values
- **Tags at Scope** — PUT `.../tags` — create/update/delete tags on any resource (foundation of cost allocation/chargeback)

## 18. Azure Migrate (`Microsoft.Migrate`)

- **Projects** — GET `.../migrateProjects` — list migration projects
- **Assessments** — GET `.../assessments` — cost/sizing assessments for on-prem → Azure migration scenarios
- **Assessed Machines** — GET `.../assessedMachines` — per-machine cost estimates and recommended SKUs
- **Groups** — GET `.../groups` — machine groups for batch cost assessment

## 19. Azure Support (`Microsoft.Support`)

- **Support Tickets** — GET/PUT `.../supportTickets` — create/manage support requests (support plan costs)
- **Services** — GET `.../services` — list Azure services available for support
- **Problem Classifications** — GET `.../problemClassifications` — issue types per service

## 20. Microsoft Graph (FinOps-Adjacent)

- **Subscribed SKUs** — GET `https://graph.microsoft.com/v1.0/subscribedSkus` — tenant license inventory
- **License Usage** — license assignment vs utilization for SaaS cost optimization
- **Directory Objects** — org structure for chargebacks

## 21. Legacy / Partner

- **Microsoft.Commerce/RateCard** — older ARM rate card (prefer Retail Prices or Price Sheets)
- **Microsoft.Commerce/UsageAggregates** — older aggregated usage data (prefer Cost Management Exports/Cost Details)
- **Partner Center Rate Card** — CSP partner pricing via Partner Center REST
- **EA Reporting APIs** (`consumption.azure.com`) — **fully retired**, replaced by Cost Management

## Key Notes

- **FOCUS**: Exports API supports FOCUS (FinOps Open Cost & Usage Specification) schema for multi-cloud standardization
- **Tag Inheritance**: Cost Management feature that propagates subscription/RG tags to child usage records
- **Scopes**: Most APIs support subscription, resource group, management group, billing account, billing profile, invoice section
- **Agreement types matter**: EA, MCA, CSP, MOSP have different available endpoints and scopes
