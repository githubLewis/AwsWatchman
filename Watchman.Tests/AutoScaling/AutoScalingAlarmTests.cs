using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;
using Amazon.CloudFormation;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Watchman.AwsResources;
using Watchman.AwsResources.Services.AutoScaling;
using Watchman.AwsResources.Services.DynamoDb;
using Watchman.Configuration;
using Watchman.Configuration.Generic;
using Watchman.Configuration.Load;
using Watchman.Engine;
using Watchman.Engine.Generation;
using Watchman.Engine.Generation.Generic;
using Watchman.Engine.Logging;
using Watchman.Tests.Fakes;
using Watchman.Tests.IoC;

namespace Watchman.Tests.AutoScaling
{
    public class AutoScalingAlarmTests
    {
        private void SetupCloudWatchDesiredMetric(Mock<IAmazonCloudWatch> client,
            int lagSeconds,
            DateTime now,
            string autoScalingGroupName,
            int value)
        {

            client.Setup(
                    c => c.GetMetricStatisticsAsync(It.Is<GetMetricStatisticsRequest>(req =>  
                            req.MetricName == "GroupDesiredCapacity"
                            && req.Namespace == "AWS/AutoScaling"
                            && req.Period == lagSeconds
                            && req.Statistics.All(s => s == "Minimum")
                            && req.Dimensions.All(
                                x => x.Name == "AutoScalingGroupName" && x.Value== autoScalingGroupName
                            )
                            && req.EndTime == now
                            && req.StartTime == now.AddSeconds(lagSeconds * -1)

                        ),
                        It.IsAny<CancellationToken>())
                )
                .ReturnsAsync(new GetMetricStatisticsResponse()
                {
                    Datapoints = new List<Datapoint>()
                    {
                        new Datapoint()
                        {
                            Minimum = value
                        }
                    }
                });
        }

        [Test]
        public async Task UsesDelayedScalingThresholdFromCloudWatch()
        {
            // arrange
            var config = ConfigHelper.CreateBasicConfiguration("test", "group-suffix",
                new AlertingGroupServices()
                {
                    AutoScaling = new AwsServiceAlarms<AutoScalingResourceConfig>()
                    {
                        Resources = new List<ResourceThresholds<AutoScalingResourceConfig>>()
                        {
                            new ResourceThresholds<AutoScalingResourceConfig>()
                            {
                                Name = "group-delay-20",
                                Options = new AutoScalingResourceConfig()
                                {
                                    InstanceCountIncreaseDelayMinutes = 20
                                }
                            },
                            new ResourceThresholds<AutoScalingResourceConfig>()
                            {
                                Name = "group-delay-100"
                            }
                        },

                        Options = new AutoScalingResourceConfig()
                        {
                            InstanceCountIncreaseDelayMinutes = 100
                        }
                    }
                });

            var cloudformation = new FakeCloudFormation();
            var ioc = new TestingIocBootstrapper()
                .WithCloudFormation(cloudformation.Instance)
                .WithConfig(config);

            ioc.GetMock<IAmazonAutoScaling>().HasAutoScalingGroups(new[]
            {
                new AutoScalingGroup()
                {
                    AutoScalingGroupName = "group-delay-20"
                },
                new AutoScalingGroup()
                {
                    AutoScalingGroupName = "group-delay-100"
                }
            });


            var now = DateTime.Parse("2018-01-26");

            ioc.GetMock<ICurrentTimeProvider>()
                .Setup(f => f.UtcNow)
                .Returns(now);

            var cloudWatch = ioc.GetMock<IAmazonCloudWatch>();

            SetupCloudWatchDesiredMetric(cloudWatch, 100 * 60, now, "group-delay-100", 90);
            SetupCloudWatchDesiredMetric(cloudWatch, 20 * 60, now, "group-delay-20", 80);

            ioc.GetMock<IConfigLoader>().HasConfig(config);

            var sut = ioc.Get<AlarmLoaderAndGenerator>();

            // act
            
            await sut.LoadAndGenerateAlarms(RunMode.GenerateAlarms);

            // assert

            var alarms = cloudformation
                .Stack("Watchman-test")
                .AlarmsByDimension("AutoScalingGroupName");

            var alarm100 = alarms["group-delay-100"].Single(a => a.Properties["AlarmName"].ToString().Contains("InService"));
            var alarm20 = alarms["group-delay-20"].Single(a => a.Properties["AlarmName"].ToString().Contains("InService"));

            var defaultAlarmThreshold = 0.5m;

            Assert.That((decimal) alarm100.Properties["Threshold"], Is.EqualTo(90 * defaultAlarmThreshold));
            Assert.That((decimal) alarm20.Properties["Threshold"], Is.EqualTo(80 * defaultAlarmThreshold));
        }

        [Test]
        public async Task UsesDesiredInstancesForThresholdByDefault()
        {
            // arrange
            var config = ConfigHelper.CreateBasicConfiguration("test", "group-suffix",
                new AlertingGroupServices()
                {
                    AutoScaling = new AwsServiceAlarms<AutoScalingResourceConfig>()
                    {
                        Resources = new List<ResourceThresholds<AutoScalingResourceConfig>>()
                        {
                            new ResourceThresholds<AutoScalingResourceConfig>()
                            {
                                Name = "group-1"
                            }
                        }
                    }
                });

            var cloudformation = new FakeCloudFormation();
            var ioc = new TestingIocBootstrapper()
                .WithCloudFormation(cloudformation.Instance)
                .WithConfig(config);

            ioc.GetMock<IAmazonAutoScaling>().HasAutoScalingGroups(new[]
            {
                new AutoScalingGroup()
                {
                    AutoScalingGroupName = "group-1",
                    DesiredCapacity = 40
                }
            });

            ioc.GetMock<IConfigLoader>().HasConfig(config);

            // todo : move to boundary registry

            var sut = ioc.Get<AlarmLoaderAndGenerator>();

            // act

            await sut.LoadAndGenerateAlarms(RunMode.GenerateAlarms);

            // assert

            var alarms = cloudformation
                .Stack("Watchman-test")
                .AlarmsByDimension("AutoScalingGroupName");

            var alarm = alarms["group-1"].Single(a => a.Properties["AlarmName"].ToString().Contains("InService"));

            var defaultAlarmThreshold = 0.5m;

            Assert.That((decimal)alarm.Properties["Threshold"], Is.EqualTo(40 * defaultAlarmThreshold));
        }

        [Test]
        public async Task UsesDesiredInstancesForThresholdIfCloudWatchMetricsDoNotExist()
        {
            // arrange
            var config = ConfigHelper.CreateBasicConfiguration("test", "group-suffix",
                new AlertingGroupServices()
                {
                    AutoScaling = new AwsServiceAlarms<AutoScalingResourceConfig>()
                    {
                        Resources = new List<ResourceThresholds<AutoScalingResourceConfig>>()
                        {
                            new ResourceThresholds<AutoScalingResourceConfig>()
                            {
                                Name = "group-1",
                                Options = new AutoScalingResourceConfig()
                                {
                                    InstanceCountIncreaseDelayMinutes = 10
                                }
                            }
                        }
                    }
                });

            var cloudformation = new FakeCloudFormation();
            var ioc = new TestingIocBootstrapper()
                .WithCloudFormation(cloudformation.Instance)
                .WithConfig(config);

            ioc.GetMock<IAmazonAutoScaling>().HasAutoScalingGroups(new[]
            {
                new AutoScalingGroup()
                {
                    AutoScalingGroupName = "group-1",
                    DesiredCapacity = 40
                }
            });
            
            var now = DateTime.UtcNow;
            
            ioc.GetMock<ICurrentTimeProvider>().Setup(a => a.UtcNow).Returns(now);
            
            ioc.GetMock<IAmazonCloudWatch>().Setup(x =>
                    x.GetMetricStatisticsAsync(It.IsAny<GetMetricStatisticsRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetMetricStatisticsResponse()
                {
                    Datapoints = new List<Datapoint>()
                });

            ioc.GetMock<IConfigLoader>().HasConfig(config);

            var sut = ioc.Get<AlarmLoaderAndGenerator>();

            // act

            await sut.LoadAndGenerateAlarms(RunMode.GenerateAlarms);

            // assert

            var alarms = cloudformation
                .Stack("Watchman-test")
                .AlarmsByDimension("AutoScalingGroupName");

            var alarm = alarms["group-1"].Single(a => a.Properties["AlarmName"].ToString().Contains("InService"));

            var defaultAlarmThreshold = 0.5m;

            Assert.That((decimal)alarm.Properties["Threshold"], Is.EqualTo(40 * defaultAlarmThreshold));
        }

        [Test]
        public async Task TimesSuppliedToCloudWatchAreUtc()
        {
            // arrange

            var config = ConfigHelper.CreateBasicConfiguration("test", "group-suffix",
                new AlertingGroupServices()
                {
                    AutoScaling = new AwsServiceAlarms<AutoScalingResourceConfig>()
                    {
                        Resources = new List<ResourceThresholds<AutoScalingResourceConfig>>()
                        {
                            new ResourceThresholds<AutoScalingResourceConfig>()
                            {
                                Name = "group-1",
                                Options = new AutoScalingResourceConfig()
                                {
                                    InstanceCountIncreaseDelayMinutes = 5
                                }
                            }
                        }
                    }
                });

            var cloudformation = new FakeCloudFormation();

            var ioc = new TestingIocBootstrapper()
                .WithCloudFormation(cloudformation.Instance)
                .WithConfig(config);

            // use real implementation to check it works
            ioc.Override<ICurrentTimeProvider>(new CurrentTimeProvider());

            ioc.GetMock<IAmazonAutoScaling>().HasAutoScalingGroups(new[]
            {
                new AutoScalingGroup()
                {
                    AutoScalingGroupName = "group-1",
                    DesiredCapacity = 40
                }
            });
            
            ioc.GetMock<IAmazonCloudWatch>()
                .Setup(c => c.GetMetricStatisticsAsync(It.IsAny<GetMetricStatisticsRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetMetricStatisticsResponse()
                {
                    Datapoints = new List<Datapoint>()
                    {
                        new Datapoint() {Minimum = 5}
                    }
                });

            var sut = ioc.Get<AlarmLoaderAndGenerator>();

            // act

            await sut.LoadAndGenerateAlarms(RunMode.GenerateAlarms);

            // assert
            ioc.GetMock<IAmazonCloudWatch>().Verify(x => x.GetMetricStatisticsAsync(
                It.Is<GetMetricStatisticsRequest>(
                    r => r.StartTime.Kind == DateTimeKind.Utc
                    && r.EndTime.Kind == DateTimeKind.Utc
                    ), It.IsAny<CancellationToken>())
                );
        }
    }
}
