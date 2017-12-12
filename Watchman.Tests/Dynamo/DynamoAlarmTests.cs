﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Watchman.AwsResources.Services.DynamoDb;
using Watchman.Configuration.Generic;
using Watchman.Engine;
using Watchman.Engine.Generation;
using Watchman.Engine.Generation.Generic;
using Watchman.Tests.Fakes;

namespace Watchman.Tests.Dynamo
{
    public class DynamoAlarmTests
    {
        private IAmazonDynamoDB CreateDynamoClientForTables(IEnumerable<TableDescription> tables)
        {
            tables = tables.ToList();

            var fakeDynamo = new Mock<IAmazonDynamoDB>();


            fakeDynamo
                .Setup(x => x.ListTablesAsync((string)null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListTablesResponse()
                {
                    TableNames = tables.Select(t => t.TableName).ToList()
                });

            foreach (var table in tables)
            {
                fakeDynamo
                    .Setup(x => x.DescribeTableAsync(table.TableName, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new DescribeTableResponse()
                    {
                        Table = table
                    });

            }

            return fakeDynamo.Object;
        }

        [Test]
        public async Task IgnoresNamedEntitiesThatDoNotExist()
        {
            // arrange

            var stack = new Mock<ICloudformationStackDeployer>();

            var dynamoClient = CreateDynamoClientForTables(new[]
            {
                new TableDescription()
                {
                    TableName = "first-dynamo-table",
                    ProvisionedThroughput = new ProvisionedThroughputDescription()
                    {
                        ReadCapacityUnits = 10,
                        WriteCapacityUnits = 10
                    }
                }
            });

            var source = new TableDescriptionSource(dynamoClient);

            var creator = new CloudFormationAlarmCreator(stack.Object);

            var config = ConfigHelper.CreateBasicConfiguration("test", "group-suffix", "DynamoDb", new List<ResourceThresholds>()
            {
                new ResourceThresholds()
                {
                    Name = "non-existant-table"
                }
            });

            var sut = IoCHelper.CreateSystemUnderTest(
                source, 
                new DynamoDbDataProvider(), 
                new DynamoDbDataProvider(),
                WatchmanServiceConfigurationMapper.MapDynamoDb,
                creator, 
                ConfigHelper.ConfigLoaderFor(config)
                );

            

            // act
            
            await sut.LoadAndGenerateAlarms(RunMode.GenerateAlarms);
            
            // assert

            stack
                .Verify(x => x.DeployStack(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>()
                    ), Times.Never);
        }

        [Test]
        public async Task AlarmCreatedWithCorrectProperties()
        {
            // arrange

            var stack = new FakeStackDeployer();

            var dynamoClient = CreateDynamoClientForTables(new[]
            {
                new TableDescription()
                {
                    TableName = "first-dynamo-table",
                    ProvisionedThroughput = new ProvisionedThroughputDescription()
                    {
                        ReadCapacityUnits = 100,
                        WriteCapacityUnits = 200
                    }
                }
            });

            var source = new TableDescriptionSource(dynamoClient);
            var creator = new CloudFormationAlarmCreator(stack);

            var config = ConfigHelper.CreateBasicConfiguration("test", "group-suffix", "DynamoDb", new List<ResourceThresholds>()
            {
                new ResourceThresholds()
                {
                    Name = "first-dynamo-table"
                }
            });

            var sut = IoCHelper.CreateSystemUnderTest(
                source,
                new DynamoDbDataProvider(),
                new DynamoDbDataProvider(),
                WatchmanServiceConfigurationMapper.MapDynamoDb,
                creator, ConfigHelper.ConfigLoaderFor(config)
            );
            
            // act

            await sut.LoadAndGenerateAlarms(RunMode.GenerateAlarms);

            // assert

            var alarmsByTable = stack
                .Stack("Watchman-test")
                .AlarmsByDimension("TableName");

            Assert.That(alarmsByTable.ContainsKey("first-dynamo-table"), Is.True);
            var alarms = alarmsByTable["first-dynamo-table"];

            Assert.That(alarms.Exists(
                alarm => 
                    alarm.Properties["MetricName"].Value<string>() == "ConsumedReadCapacityUnits"
                    && alarm.Properties["AlarmName"].Value<string>().Contains("ConsumedReadCapacityUnitsHigh")
                    && alarm.Properties["Threshold"].Value<int>() == 100 * 0.8m
                    && alarm.Properties["Period"].Value<int>() == 60
                    && alarm.Properties["ComparisonOperator"].Value<string>() == "GreaterThanOrEqualToThreshold"
                    && alarm.Properties["Statistic"].Value<string>() == "Sum"
                    && alarm.Properties["Namespace"].Value<string>() == AwsNamespace.DynamoDb
                    )
                );

            Assert.That(alarms.Exists(
                alarm =>
                    alarm.Properties["MetricName"].Value<string>() == "ConsumedWriteCapacityUnits"
                    && alarm.Properties["AlarmName"].Value<string>().Contains("ConsumedWriteCapacityUnitsHigh")
                    && alarm.Properties["Threshold"].Value<int>() == 200 * 0.8m
                    && alarm.Properties["Period"].Value<int>() == 60
                    && alarm.Properties["ComparisonOperator"].Value<string>() == "GreaterThanOrEqualToThreshold"
                    && alarm.Properties["Statistic"].Value<string>() == "Sum"
                    && alarm.Properties["Namespace"].Value<string>() == AwsNamespace.DynamoDb
                )
            );

            Assert.That(alarms.Exists(
                alarm =>
                    alarm.Properties["MetricName"].Value<string>() == "ThrottledRequests"
                    && alarm.Properties["AlarmName"].Value<string>().Contains("ThrottledRequestsHigh")
                    && alarm.Properties["Threshold"].Value<int>() == 5
                    && alarm.Properties["Period"].Value<int>() == 60
                    && alarm.Properties["ComparisonOperator"].Value<string>() == "GreaterThanOrEqualToThreshold"
                    && alarm.Properties["Statistic"].Value<string>() == "Sum"
                    && alarm.Properties["Namespace"].Value<string>() == AwsNamespace.DynamoDb
                )
            );

        }


        [Test]
        public async Task CreatesExpectedDefaultDynamoAlarms()
        {
            // arrange

            var stack = new FakeStackDeployer();

            var dynamoClient = CreateDynamoClientForTables(new[]
            {
                new TableDescription()
                {
                    TableName = "first-dynamo-table",
                    ProvisionedThroughput = new ProvisionedThroughputDescription()
                    {
                        ReadCapacityUnits = 100,
                        WriteCapacityUnits = 200
                    }
                },
                new TableDescription()
                {
                    TableName = "second-dynamo-table",
                    ProvisionedThroughput = new ProvisionedThroughputDescription()
                    {
                        ReadCapacityUnits = 1000,
                        WriteCapacityUnits = 2000
                    }
                }
            });

            var source = new TableDescriptionSource(dynamoClient);
            var creator = new CloudFormationAlarmCreator(stack);

            var config = ConfigHelper.CreateBasicConfiguration("test", "group-suffix", "DynamoDb", new List<ResourceThresholds>()
            {
                new ResourceThresholds()
                {
                    Name = "first-dynamo-table"
                },
                new ResourceThresholds()
                {
                    Pattern = "second"
                }
            });

            var sut = IoCHelper.CreateSystemUnderTest(
                source,
                new DynamoDbDataProvider(),
                new DynamoDbDataProvider(),
                WatchmanServiceConfigurationMapper.MapDynamoDb,
                creator, ConfigHelper.ConfigLoaderFor(config)
            );



            // act

            await sut.LoadAndGenerateAlarms(RunMode.GenerateAlarms);

            // assert

            Assert.That(stack.StackWasDeployed("Watchman-test"), Is.True);

            var result = stack.StackJson("Watchman-test");
            var stack2 = JsonConvert.DeserializeObject<Template>(result);

            var byAlarm = stack2.AlarmsByDimension("TableName");

            Assert.That(byAlarm.ContainsKey("first-dynamo-table"), Is.True);

            var firstTable = byAlarm["first-dynamo-table"];

            var x = firstTable.First().Properties["MetricName"];

            Assert.That(firstTable.Exists(
                alarm =>
                    alarm.Properties["MetricName"].Value<string>() == "ConsumedReadCapacityUnits"
                    && alarm.Properties["Threshold"].Value<int>() == 100 * 0.8m
                    && alarm.Properties["Period"].Value<int>() == 60
                    && alarm.Properties["ComparisonOperator"].Value<string>() == "GreaterThanOrEqualToThreshold"
                    && alarm.Properties["Statistic"].Value<string>() == "Sum"
                    && alarm.Properties["Namespace"].Value<string>() == AwsNamespace.DynamoDb
                    )
                );


        }
    }
}
